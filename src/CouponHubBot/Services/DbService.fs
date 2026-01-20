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

[<CLIMutable>]
type PendingAdd =
    { id: Guid
      owner_id: int64
      photo_file_id: string
      value: decimal
      min_check: decimal
      expires_at: DateOnly
      created_at: DateTime }

type DbService(connString: string) =
    let openConn() = task {
        let conn = new NpgsqlConnection(connString)
        do! conn.OpenAsync()
        return conn
    }

    let insertEvent (conn: NpgsqlConnection) (tx: IDbTransaction) (couponId: int) (userId: int64) (eventType: string) =
        //language=postgresql
        let sql =
            """
INSERT INTO coupon_event (coupon_id, user_id, event_type)
VALUES (@coupon_id, @user_id, @event_type);
"""
        conn.ExecuteAsync(sql, {| coupon_id = couponId; user_id = userId; event_type = eventType |}, tx) |> taskIgnore

    member _.UpsertUser(user: DbUser) =
        task {
            use! conn = openConn()
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

    member _.AddCoupon(ownerId, photoFileId, value, minCheck: decimal, expiresAt, barcodeText) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
INSERT INTO coupon (owner_id, photo_file_id, value, min_check, expires_at, barcode_text, status)
VALUES (@owner_id, @photo_file_id, @value, @min_check, @expires_at, @barcode_text, 'available')
RETURNING *;
"""
            use tx = conn.BeginTransaction()
            let! coupon =
                conn.QuerySingleAsync<Coupon>(
                    sql,
                    {| owner_id = ownerId
                       photo_file_id = photoFileId
                       value = value
                       min_check = minCheck
                       expires_at = expiresAt
                       barcode_text = barcodeText |},
                    tx)
            do! insertEvent conn tx coupon.id ownerId "added"
            do! tx.CommitAsync()
            return coupon
        }

    member _.GetAvailableCoupons() =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT *
FROM coupon
WHERE status = 'available'
ORDER BY expires_at, id;
"""
            let! coupons = conn.QueryAsync<Coupon>(sql)
            return coupons |> Seq.toArray
        }

    member _.GetCouponById(couponId) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = "SELECT * FROM coupon WHERE id = @coupon_id"
            let! coupons = conn.QueryAsync<Coupon>(sql, {| coupon_id = couponId |})
            return coupons |> Seq.tryHead
        }
        
    member _.GetUserById(userId) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = """SELECT * FROM "user" WHERE id = @user_id"""
            let! users = conn.QueryAsync<DbUser>(sql, {| user_id = userId |})
            return users |> Seq.tryHead
        }

    member _.GetCouponsByOwner(ownerId) =
        task {
            use! conn = openConn()
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
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT *
FROM coupon
WHERE taken_by = @user_id
  AND status = 'taken'
ORDER BY taken_at DESC NULLS LAST, id DESC;
"""
            let! coupons = conn.QueryAsync<Coupon>(sql, {| user_id = userId |})
            return coupons |> Seq.toArray
        }

    member _.GetUserStats(userId) =
        task {
            use! conn = openConn()
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
            let! row = conn.QuerySingleAsync<{| added: int64; taken: int64; used: int64 |}>(sql, {| user_id = userId |})
            return int row.added, int row.taken, int row.used
        }

    member _.TryTakeCoupon(couponId, takerId) =
        task {
            use! conn = openConn()
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
                    tx
                )

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
            use! conn = openConn()
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
            use! conn = openConn()
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
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT *
FROM coupon
WHERE status = 'available'
AND expires_at = CURRENT_DATE
ORDER BY id;
"""
            let! coupons = conn.QueryAsync<Coupon>(sql)
            return coupons |> Seq.toArray
        }

    member _.CreatePendingAdd(id: Guid, ownerId: int64, photoFileId: string, value: decimal, minCheck: decimal, expiresAt: DateOnly) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
INSERT INTO pending_add (id, owner_id, photo_file_id, value, min_check, expires_at)
VALUES (@id, @owner_id, @photo_file_id, @value, @min_check, @expires_at);
"""
            let! _ =
                conn.ExecuteAsync(
                    sql,
                    {| id = id
                       owner_id = ownerId
                       photo_file_id = photoFileId
                       value = value
                       min_check = minCheck
                       expires_at = expiresAt |}
                )
            return ()
        }

    member _.ConsumePendingAdd(id: Guid) =
        task {
            use! conn = openConn()
            use tx = conn.BeginTransaction()

            // Lock row so concurrent confirmations don't double-consume
            //language=postgresql
            let selectSql =
                """
SELECT *
FROM pending_add
WHERE id = @id
FOR UPDATE;
"""

            let! pending =
                conn.QuerySingleOrDefaultAsync<PendingAdd>(
                    selectSql,
                    {| id = id |},
                    tx
                )

            if obj.ReferenceEquals(pending, null) then
                do! tx.RollbackAsync()
                return None
            else
                // delete consumed row
                //language=postgresql
                let deleteSql = "DELETE FROM pending_add WHERE id = @id;"
                let! _ = conn.ExecuteAsync(deleteSql, {| id = id |}, tx)
                do! tx.CommitAsync()
                return Some pending
        }
