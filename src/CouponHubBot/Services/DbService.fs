namespace CouponHubBot.Services

open System
open System.Data
open System.Threading.Tasks
open Dapper
open Npgsql
open CouponHubBot
open CouponHubBot.Utils

type TakeCouponResult =
    | Taken of Coupon
    | NotFoundOrNotAvailable

type IDbService =
    abstract member UpsertUser: DbUser -> Task<DbUser>
    abstract member AddCoupon: ownerId:int64 * photoFileId:string * value:decimal * expiresAt:DateOnly * barcodeText:string | null -> Task<Coupon>
    abstract member GetAvailableCoupons: unit -> Task<Coupon array>
    abstract member GetCouponById: couponId:int -> Task<Coupon option>
    abstract member GetUserById: userId:int64 -> Task<DbUser option>
    abstract member GetCouponsByOwner: ownerId:int64 -> Task<Coupon array>
    abstract member GetCouponsTakenBy: userId:int64 -> Task<Coupon array>
    abstract member GetUserStats: userId:int64 -> Task<int * int * int> // added, taken, used
    abstract member TryTakeCoupon: couponId:int * takerId:int64 -> Task<TakeCouponResult>
    abstract member MarkUsed: couponId:int * userId:int64 -> Task<bool>
    abstract member ReturnToAvailable: couponId:int * userId:int64 -> Task<bool>
    abstract member GetExpiringTodayAvailable: unit -> Task<Coupon array>

type DbService(connString: string) =
    let openConn () =
        let conn = new NpgsqlConnection(connString)
        conn.Open()
        conn

    let insertEvent (conn: NpgsqlConnection) (tx: IDbTransaction) (couponId: int) (userId: int64) (eventType: string) =
        //language=postgresql
        let sql =
            """
INSERT INTO coupon_event (coupon_id, user_id, event_type)
VALUES (@coupon_id, @user_id, @event_type);
"""
        conn.ExecuteAsync(sql, {| coupon_id = couponId; user_id = userId; event_type = eventType |}, tx) |> taskIgnore

    interface IDbService with
        member _.UpsertUser(user: DbUser) =
            task {
                use conn = openConn ()
                //language=postgresql
                let sql =
                    """
INSERT INTO "user" (id, username, first_name, last_name, created_at, updated_at)
VALUES (@id, @username, @first_name, @last_name, @created_at, @updated_at)
ON CONFLICT (id) DO UPDATE
    SET username   = EXCLUDED.username,
        first_name = EXCLUDED.first_name,
        last_name  = EXCLUDED.last_name,
        updated_at = GREATEST(EXCLUDED.updated_at, "user".updated_at)
RETURNING *;
"""
                let! inserted = conn.QuerySingleAsync<DbUser>(sql, user)
                return inserted
            }

        member _.AddCoupon(ownerId, photoFileId, value, expiresAt, barcodeText) =
            task {
                use conn = openConn ()
                //language=postgresql
                let sql =
                    """
INSERT INTO coupon (owner_id, photo_file_id, value, expires_at, barcode_text, status)
VALUES (@owner_id, @photo_file_id, @value, @expires_at, @barcode_text, 'available')
RETURNING *;
"""
                use tx = conn.BeginTransaction()
                let! coupon =
                    conn.QuerySingleAsync<Coupon>(
                        sql,
                        {| owner_id = ownerId
                           photo_file_id = photoFileId
                           value = value
                           expires_at = expiresAt
                           barcode_text = barcodeText |},
                        tx)
                do! insertEvent conn tx coupon.id ownerId "added"
                do! tx.CommitAsync()
                return coupon
            }

        member _.GetAvailableCoupons() =
            task {
                use conn = openConn ()
                //language=postgresql
                let sql =
                    """
SELECT *
FROM coupon
WHERE status = 'available'
ORDER BY expires_at ASC, id ASC;
"""
                let! coupons = conn.QueryAsync<Coupon>(sql)
                return coupons |> Seq.toArray
            }

        member _.GetCouponById(couponId) =
            task {
                use conn = openConn ()
                //language=postgresql
                let sql = "SELECT * FROM coupon WHERE id = @coupon_id"
                let! coupons = conn.QueryAsync<Coupon>(sql, {| coupon_id = couponId |})
                return coupons |> Seq.tryHead
            }
            
        member _.GetUserById(userId) =
            task {
                use conn = openConn ()
                //language=postgresql
                let sql = """SELECT * FROM "user" WHERE id = @user_id"""
                let! users = conn.QueryAsync<DbUser>(sql, {| user_id = userId |})
                return users |> Seq.tryHead
            }

        member _.GetCouponsByOwner(ownerId) =
            task {
                use conn = openConn ()
                //language=postgresql
                let sql =
                    """
SELECT *
FROM coupon
WHERE owner_id = @owner_id
ORDER BY created_at DESC, id DESC;
"""
                let! coupons = conn.QueryAsync<Coupon>(sql, {| owner_id = ownerId |})
                return coupons |> Seq.toArray
            }

        member _.GetCouponsTakenBy(userId) =
            task {
                use conn = openConn ()
                //language=postgresql
                let sql =
                    """
SELECT *
FROM coupon
WHERE taken_by = @user_id
ORDER BY taken_at DESC NULLS LAST, id DESC;
"""
                let! coupons = conn.QueryAsync<Coupon>(sql, {| user_id = userId |})
                return coupons |> Seq.toArray
            }

        member _.GetUserStats(userId) =
            task {
                use conn = openConn ()
                //language=postgresql
                let sql =
                    """
WITH added AS (
    SELECT COUNT(*) AS c FROM coupon WHERE owner_id = @user_id
),
taken AS (
    SELECT COUNT(*) AS c FROM coupon WHERE taken_by = @user_id
),
used AS (
    SELECT COUNT(*) AS c FROM coupon WHERE taken_by = @user_id AND status = 'used'
)
SELECT (SELECT c FROM added) AS added,
       (SELECT c FROM taken) AS taken,
       (SELECT c FROM used)  AS used;
"""
                let! row = conn.QuerySingleAsync<{| added: int; taken: int; used: int |}>(sql, {| user_id = userId |})
                return row.added, row.taken, row.used
            }

        member _.TryTakeCoupon(couponId, takerId) =
            task {
                use conn = openConn ()
                use tx = conn.BeginTransaction(IsolationLevel.ReadCommitted)

                // Atomic: only one taker wins (status must be available)
                //language=postgresql
                let sql =
                    """
UPDATE coupon
SET status = 'taken',
    taken_by = @taker_id,
    taken_at = timezone('utc'::TEXT, NOW())
WHERE id = @coupon_id
  AND status = 'available'
RETURNING *;
"""
                let! updated =
                    conn.QueryAsync<Coupon>(
                        sql,
                        {| coupon_id = couponId
                           taker_id = takerId |},
                        tx)

                match updated |> Seq.tryHead with
                | None ->
                    do! tx.RollbackAsync()
                    return NotFoundOrNotAvailable
                | Some coupon ->
                    do! insertEvent conn tx coupon.id takerId "taken"
                    do! tx.CommitAsync()
                    return Taken coupon
            }

        member _.MarkUsed(couponId, userId) =
            task {
                use conn = openConn ()
                use tx = conn.BeginTransaction(IsolationLevel.ReadCommitted)

                //language=postgresql
                let sql =
                    """
UPDATE coupon
SET status = 'used'
WHERE id = @coupon_id
  AND status = 'taken'
  AND taken_by = @user_id;
"""
                let! rows = conn.ExecuteAsync(sql, {| coupon_id = couponId; user_id = userId |}, tx)
                if rows = 1 then
                    do! insertEvent conn tx couponId userId "used"
                    do! tx.CommitAsync()
                    return true
                else
                    do! tx.RollbackAsync()
                    return false
            }

        member _.ReturnToAvailable(couponId, userId) =
            task {
                use conn = openConn ()
                use tx = conn.BeginTransaction(IsolationLevel.ReadCommitted)

                //language=postgresql
                let sql =
                    """
UPDATE coupon
SET status = 'available',
    taken_by = NULL,
    taken_at = NULL
WHERE id = @coupon_id
  AND status = 'taken'
  AND taken_by = @user_id;
"""
                let! rows = conn.ExecuteAsync(sql, {| coupon_id = couponId; user_id = userId |}, tx)
                if rows = 1 then
                    do! insertEvent conn tx couponId userId "returned"
                    do! tx.CommitAsync()
                    return true
                else
                    do! tx.RollbackAsync()
                    return false
            }

        member _.GetExpiringTodayAvailable() =
            task {
                use conn = openConn ()
                //language=postgresql
                let sql =
                    """
SELECT *
FROM coupon
WHERE status = 'available'
  AND expires_at = CURRENT_DATE
ORDER BY id ASC;
"""
                let! coupons = conn.QueryAsync<Coupon>(sql)
                return coupons |> Seq.toArray
            }

