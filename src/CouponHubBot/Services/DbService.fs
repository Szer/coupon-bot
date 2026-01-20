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
    | LimitReached

[<CLIMutable>]
type PendingAddFlow =
    { user_id: int64
      stage: string
      photo_file_id: string | null
      value: Nullable<decimal>
      min_check: Nullable<decimal>
      expires_at: Nullable<DateOnly>
      updated_at: DateTime }

[<CLIMutable>]
type UserEventCount =
    { user_id: int64
      username: string | null
      first_name: string | null
      count: int64 }

[<CLIMutable>]
type OverdueTakenUser =
    { user_id: int64
      overdue_count: int }

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
  AND expires_at >= CURRENT_DATE
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

            // Serialize concurrent take attempts for the same user to avoid race conditions on the 4-coupons limit.
            //language=postgresql
            let lockUserSql =
                """
SELECT id
FROM "user"
WHERE id = @taker_id
FOR UPDATE;
"""
            let! _lockedUser =
                conn.QuerySingleOrDefaultAsync<int64>(
                    lockUserSql,
                    {| taker_id = takerId |},
                    tx
                )

            // Enforce max 4 simultaneously taken coupons per user.
            //language=postgresql
            let countSql =
                """
SELECT COUNT(*)::int
FROM coupon
WHERE taken_by = @taker_id
  AND status = 'taken';
"""
            let! takenCount =
                conn.QuerySingleAsync<int>(
                    countSql,
                    {| taker_id = takerId |},
                    tx
                )

            if takenCount >= 4 then
                do! tx.RollbackAsync()
                return LimitReached
            else
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
  AND expires_at >= CURRENT_DATE
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

    member _.GetUsersWithOverdueTakenCoupons(nowUtc: DateTime, minAge: TimeSpan) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT taken_by AS user_id, COUNT(*)::int AS overdue_count
FROM coupon
WHERE status = 'taken'
  AND taken_by IS NOT NULL
  AND taken_at IS NOT NULL
  AND taken_at <= (@now_utc - (@min_age_seconds * interval '1 second'))
GROUP BY taken_by
ORDER BY taken_by;
"""
            let! rows =
                conn.QueryAsync<OverdueTakenUser>(
                    sql,
                    {| now_utc = nowUtc
                       min_age_seconds = int64 minAge.TotalSeconds |}
                )
            return rows |> Seq.toArray
        }

    member _.GetUserEventCounts(eventType: string, sinceUtc: DateTime, untilUtc: DateTime) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT e.user_id,
       u.username,
       u.first_name,
       COUNT(*)::bigint AS count
FROM coupon_event e
JOIN "user" u ON u.id = e.user_id
WHERE e.event_type = @event_type
  AND e.created_at >= @since_utc
  AND e.created_at < @until_utc
GROUP BY e.user_id, u.username, u.first_name
ORDER BY count DESC, e.user_id;
"""
            let! rows =
                conn.QueryAsync<UserEventCount>(
                    sql,
                    {| event_type = eventType
                       since_utc = sinceUtc
                       until_utc = untilUtc |}
                )
            return rows |> Seq.toArray
        }

    member _.GetPendingAddFlow(userId: int64) =
        task {
            use! conn = openConn()
            // Expire after 1 hour of inactivity.
            //language=postgresql
            let expireSql =
                """
DELETE FROM pending_add
WHERE user_id = @user_id
  AND updated_at < (timezone('utc'::TEXT, NOW()) - interval '1 hour');
"""
            let! _ = conn.ExecuteAsync(expireSql, {| user_id = userId |})

            //language=postgresql
            let sql =
                """
SELECT *
FROM pending_add
WHERE user_id = @user_id;
"""
            let! row = conn.QuerySingleOrDefaultAsync<PendingAddFlow>(sql, {| user_id = userId |})
            if obj.ReferenceEquals(row, null) then
                return None
            else
                return Some row
        }

    member _.UpsertPendingAddFlow(flow: PendingAddFlow) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
INSERT INTO pending_add (user_id, stage, photo_file_id, value, min_check, expires_at, updated_at)
VALUES (@user_id, @stage, @photo_file_id, @value, @min_check, @expires_at, timezone('utc'::TEXT, NOW()))
ON CONFLICT (user_id) DO UPDATE
SET stage = EXCLUDED.stage,
    photo_file_id = EXCLUDED.photo_file_id,
    value = EXCLUDED.value,
    min_check = EXCLUDED.min_check,
    expires_at = EXCLUDED.expires_at,
    updated_at = EXCLUDED.updated_at;
"""
            let! _ = conn.ExecuteAsync(sql, flow)
            return ()
        }

    member _.ClearPendingAddFlow(userId: int64) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = "DELETE FROM pending_add WHERE user_id = @user_id;"
            let! _ = conn.ExecuteAsync(sql, {| user_id = userId |})
            return ()
        }

    member _.SetPendingFeedback(userId: int64) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
INSERT INTO pending_feedback (user_id)
VALUES (@user_id)
ON CONFLICT (user_id) DO UPDATE
SET created_at = EXCLUDED.created_at;
"""
            let! _ = conn.ExecuteAsync(sql, {| user_id = userId |})
            return ()
        }

    /// Deletes pending flag and returns true if it existed.
    member _.TryConsumePendingFeedback(userId: int64) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
DELETE FROM pending_feedback
WHERE user_id = @user_id
RETURNING user_id;
"""
            let! deleted = conn.QueryAsync<int64>(sql, {| user_id = userId |})
            return deleted |> Seq.isEmpty |> not
        }

    member _.ClearPendingFeedback(userId: int64) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = "DELETE FROM pending_feedback WHERE user_id = @user_id;"
            let! _ = conn.ExecuteAsync(sql, {| user_id = userId |})
            return ()
        }
