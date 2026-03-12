namespace CouponHubBot.Services

open System
open System.Data
open System.Runtime.ExceptionServices
open System.Threading.Tasks
open Dapper
open Npgsql
open CouponHubBot
open CouponHubBot.Utils

type TakeCouponResult =
    | Taken of Coupon
    | NotFoundOrNotAvailable
    | LimitReached

[<RequireQualifiedAccess>]
type AddCouponResult =
    | Added of Coupon
    | Expired
    | DuplicatePhoto of existingCouponId: int
    | DuplicateBarcode of existingCouponId: int

[<RequireQualifiedAccess>]
type VoidCouponResult =
    | Voided of coupon: Coupon * takenByUserId: int64 option
    | NotFoundOrNotAllowed

[<CLIMutable>]
type PendingAddFlow =
    { user_id: int64
      stage: string
      photo_file_id: string | null
      value: Nullable<decimal>
      min_check: Nullable<decimal>
      expires_at: Nullable<DateOnly>
      barcode_text: string | null
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

[<CLIMutable>]
type EventTypeCountRow =
    { event_type: string
      count: int64 }

[<CLIMutable>]
type ChatMessageRow =
    { user_id: int64
      message_id: int
      text: string | null
      has_photo: bool
      has_document: bool
      reply_to_message_id: Nullable<int>
      created_at: DateTime }

type DbService(connString: string, timeProvider: TimeProvider, maxTakenCoupons: int) =
    let utcNow () = timeProvider.GetUtcNow().UtcDateTime
    let todayUtc () = DateOnly.FromDateTime(utcNow ())

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

    member _.TryAddCoupon(ownerId, photoFileId, value, minCheck: decimal, expiresAt: DateOnly, barcodeText: string | null) =
        task {
            let todayUtc = todayUtc ()
            if expiresAt < todayUtc then
                return AddCouponResult.Expired
            else
                use! conn = openConn()
                use tx = conn.BeginTransaction(IsolationLevel.ReadCommitted)

                // Check duplicate by photo_file_id (always).
                //language=postgresql
                let dupPhotoSql =
                    """
SELECT id
FROM coupon
WHERE photo_file_id = @photo_file_id
LIMIT 1;
"""

                let! dupPhoto =
                    conn.QuerySingleOrDefaultAsync<int>(
                        dupPhotoSql,
                        {| photo_file_id = photoFileId |},
                        tx
                    )

                if dupPhoto <> 0 then
                    do! tx.RollbackAsync()
                    return AddCouponResult.DuplicatePhoto dupPhoto
                else
                    // Check duplicate by barcode only when barcode is known.
                    let hasBarcode = not (String.IsNullOrWhiteSpace barcodeText)
                    
                    // Query duplicate barcode id only when barcode is known; otherwise treat as no-dup (0).
                    //language=postgresql
                    let dupBarcodeSql =
                        """
SELECT id
FROM coupon
WHERE barcode_text = @barcode_text
  AND expires_at >= @today
LIMIT 1;
"""

                    let! dupBarcode =
                        if hasBarcode then
                            conn.QuerySingleOrDefaultAsync<int>(
                                dupBarcodeSql,
                                {| barcode_text = barcodeText
                                   today = todayUtc |},
                                tx
                            )
                        else
                            Task.FromResult 0

                    if dupBarcode <> 0 then
                        do! tx.RollbackAsync()
                        return AddCouponResult.DuplicateBarcode dupBarcode
                    else

                        // Insert coupon.
                        //language=postgresql
                        let insertSql =
                            """
INSERT INTO coupon (owner_id, photo_file_id, value, min_check, expires_at, barcode_text, status)
VALUES (@owner_id, @photo_file_id, @value, @min_check, @expires_at, @barcode_text, 'available')
RETURNING *;
"""

                        try
                            let! coupon =
                                conn.QuerySingleAsync<Coupon>(
                                    insertSql,
                                    {| owner_id = ownerId
                                       photo_file_id = photoFileId
                                       value = value
                                       min_check = minCheck
                                       expires_at = expiresAt
                                       barcode_text = barcodeText |},
                                    tx
                                )
                            do! insertEvent conn tx coupon.id ownerId "added"
                            do! tx.CommitAsync()
                            return AddCouponResult.Added coupon
                        with
                        | :? PostgresException as pgEx
                            when pgEx.SqlState = "23505"
                                 && pgEx.ConstraintName = "coupon_barcode_active_uniq" ->
                            do! tx.RollbackAsync()
                            // Race condition: another transaction inserted the same barcode concurrently.
                            // Look up the winning coupon by the exact constraint key to return its ID.
                            //language=postgresql
                            let dupBarcodeByKeySql =
                                """
SELECT id
FROM coupon
WHERE barcode_text = @barcode_text
  AND expires_at = @expires_at
  AND status IN ('available', 'taken')
ORDER BY id
LIMIT 1;
"""
                            let! existingId =
                                conn.QuerySingleOrDefaultAsync<int>(
                                    dupBarcodeByKeySql,
                                    {| barcode_text = barcodeText
                                       expires_at = expiresAt |}
                                )
                            if existingId = 0 then
                                // The winning row was not found — the concurrent transaction may have
                                // rolled back by the time we looked. Re-raise preserving the stack trace.
                                ExceptionDispatchInfo.Throw pgEx
                                return Unchecked.defaultof<AddCouponResult>
                            else
                                return AddCouponResult.DuplicateBarcode existingId
                        | :? PostgresException as pgEx
                            when pgEx.SqlState = "23505"
                                 && pgEx.ConstraintName = "coupon_photo_file_id_uniq" ->
                            do! tx.RollbackAsync()
                            // Race condition: another transaction inserted the same photo concurrently.
                            // Look up the winning coupon to return its ID.
                            let! existingId =
                                conn.QuerySingleOrDefaultAsync<int>(
                                    dupPhotoSql,
                                    {| photo_file_id = photoFileId |}
                                )
                            if existingId = 0 then
                                // The winning row was not found — the concurrent transaction may have
                                // rolled back by the time we looked. Re-raise preserving the stack trace.
                                ExceptionDispatchInfo.Throw pgEx
                                return Unchecked.defaultof<AddCouponResult>
                            else
                                return AddCouponResult.DuplicatePhoto existingId
        }

    member _.GetAvailableCoupons() =
        task {
            use! conn = openConn()
            let today = todayUtc ()
            //language=postgresql
            let sql =
                """
SELECT *
FROM coupon
WHERE status = 'available'
  AND expires_at >= @today
ORDER BY expires_at, id;
"""
            let! coupons = conn.QueryAsync<Coupon>(sql, {| today = today |})
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
SELECT event_type, COUNT(*)::bigint AS count
FROM coupon_event
WHERE user_id = @user_id
GROUP BY event_type;
"""
            let! rows = conn.QueryAsync<EventTypeCountRow>(sql, {| user_id = userId |})

            let counts =
                rows
                |> Seq.fold (fun acc r -> acc |> Map.add r.event_type r.count) Map.empty

            let get (eventType: string) =
                counts
                |> Map.tryFind eventType
                |> Option.defaultValue 0L

            return get "added", get "taken", get "returned", get "used", get "voided"
        }

    member _.TryTakeCoupon(couponId, takerId) =
        task {
            use! conn = openConn()
            use tx = conn.BeginTransaction(IsolationLevel.ReadCommitted)
            let today = todayUtc ()
            let takenAt = utcNow ()

            // Serialize concurrent take attempts for the same user to avoid race conditions on the coupons limit.
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

            // Enforce max simultaneously taken coupons per user.
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

            if takenCount >= maxTakenCoupons then
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
    taken_at = @taken_at
WHERE id = @coupon_id
  AND status = 'available'
  AND expires_at >= @today
RETURNING *;
"""
            let! updated =
                conn.QueryAsync<Coupon>(
                    sql,
                    {| coupon_id = couponId
                       taker_id = takerId
                       taken_at = takenAt
                       today = today |},
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
            let today = todayUtc ()
            //language=postgresql
            let sql =
                """
SELECT *
FROM coupon
WHERE status = 'available'
AND expires_at = @today
ORDER BY id;
"""
            let! coupons = conn.QueryAsync<Coupon>(sql, {| today = today |})
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

    member _.GetUsersWhoUsedButDidNotAddYesterday(nowUtc: DateTime) =
        task {
            use! conn = openConn()
            // Calculate yesterday's date range in UTC
            let today = DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, DateTimeKind.Utc)
            let yesterdayStart = today.AddDays(-1.0)
            let yesterdayEnd = today
            
            //language=postgresql
            let sql =
                """
SELECT DISTINCT u.user_id
FROM (
    SELECT user_id, MAX(created_at) AS last_used_at
    FROM coupon_event
    WHERE event_type = 'used'
      AND created_at >= @yesterday_start
      AND created_at < @yesterday_end
    GROUP BY user_id
) u
WHERE NOT EXISTS (
    SELECT 1
    FROM coupon_event e
    WHERE e.user_id = u.user_id
      AND e.event_type = 'added'
      AND e.created_at > u.last_used_at
)
ORDER BY u.user_id;
"""
            let! userIds =
                conn.QueryAsync<int64>(
                    sql,
                    {| yesterday_start = yesterdayStart
                       yesterday_end = yesterdayEnd |}
                )
            return userIds |> Seq.toArray
        }

    member _.GetPendingAddFlow(userId: int64) =
        task {
            use! conn = openConn()
            let nowUtc = utcNow ()
            // Expire after 1 hour of inactivity.
            //language=postgresql
            let expireSql =
                """
DELETE FROM pending_add
WHERE user_id = @user_id
  AND updated_at < (@now_utc - interval '1 hour');
"""
            let! _ = conn.ExecuteAsync(expireSql, {| user_id = userId; now_utc = nowUtc |})

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
INSERT INTO pending_add (user_id, stage, photo_file_id, value, min_check, expires_at, barcode_text, updated_at)
VALUES (@user_id, @stage, @photo_file_id, @value, @min_check, @expires_at, @barcode_text, @updated_at)
ON CONFLICT (user_id) DO UPDATE
SET stage = EXCLUDED.stage,
    photo_file_id = EXCLUDED.photo_file_id,
    value = EXCLUDED.value,
    min_check = EXCLUDED.min_check,
    expires_at = EXCLUDED.expires_at,
    barcode_text = EXCLUDED.barcode_text,
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

    member _.VoidCoupon(couponId: int, userId: int64, isAdmin: bool) =
        task {
            use! conn = openConn()
            use tx = conn.BeginTransaction(IsolationLevel.ReadCommitted)
            let today = todayUtc ()

            // First, read the coupon to capture taken_by before we clear it.
            //language=postgresql
            let selectSql =
                """
SELECT *
FROM coupon
WHERE id = @coupon_id
  AND status IN ('available', 'taken')
  AND expires_at >= @today
  AND (@is_admin OR owner_id = @user_id)
FOR UPDATE;
"""
            let! selectRows =
                conn.QueryAsync<Coupon>(
                    selectSql,
                    {| coupon_id = couponId
                       today = today
                       user_id = userId
                       is_admin = isAdmin |},
                    tx
                )

            match selectRows |> Seq.tryHead with
            | None ->
                do! tx.RollbackAsync()
                return VoidCouponResult.NotFoundOrNotAllowed
            | Some original ->
                let takenBy =
                    if original.status = "taken" && original.taken_by.HasValue then
                        Some original.taken_by.Value
                    else
                        None

                //language=postgresql
                let updateSql =
                    """
UPDATE coupon
SET status = 'voided',
    taken_by = NULL,
    taken_at = NULL
WHERE id = @coupon_id;
"""
                let! _ = conn.ExecuteAsync(updateSql, {| coupon_id = couponId |}, tx)

                do! insertEvent conn tx couponId original.owner_id "voided"
                do! tx.CommitAsync()
                return VoidCouponResult.Voided ({ original with status = "voided"; taken_by = Nullable(); taken_at = Nullable() }, takenBy)
        }

    member _.GetVoidableCouponsByOwner(ownerId: int64) =
        task {
            use! conn = openConn()
            let today = todayUtc ()
            //language=postgresql
            let sql =
                """
SELECT *
FROM coupon
WHERE owner_id = @owner_id
  AND status IN ('available', 'taken')
  AND expires_at >= @today
ORDER BY expires_at, id;
"""
            let! coupons = conn.QueryAsync<Coupon>(sql, {| owner_id = ownerId; today = today |})
            return coupons |> Seq.toArray
        }

    member _.GetCouponEventHistory(couponId: int) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = """
SELECT TO_CHAR(ce.created_at, 'YYYY-MM-DD HH24:MI:SS') AS date,
       COALESCE(u.username, COALESCE(u.first_name, '') || COALESCE(u.last_name, '')) AS "user",
       ce.event_type
FROM coupon_event ce
         JOIN public."user" u ON u.id = ce.user_id
WHERE ce.coupon_id = @couponId
ORDER BY ce.created_at;
"""
            let! rows = conn.QueryAsync<CouponEventHistoryRow>(sql, {| couponId = couponId |})
            return rows |> Seq.toArray
        }

    // ── Chat message monitoring ──────────────────────────────────────

    member _.SaveChatMessage(chatId: int64, messageId: int, userId: int64, text: string | null, hasPhoto: bool, hasDocument: bool, replyToMessageId: Nullable<int>) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
INSERT INTO chat_message (chat_id, message_id, user_id, text, has_photo, has_document, reply_to_message_id)
VALUES (@chat_id, @message_id, @user_id, @text, @has_photo, @has_document, @reply_to_message_id)
ON CONFLICT (chat_id, message_id) DO NOTHING;
"""
            let! _ = conn.ExecuteAsync(sql,
                {| chat_id = chatId
                   message_id = messageId
                   user_id = userId
                   text = text
                   has_photo = hasPhoto
                   has_document = hasDocument
                   reply_to_message_id = replyToMessageId |})
            return ()
        }

    member _.DeleteOldChatMessages(olderThan: DateTime) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = "DELETE FROM chat_message WHERE created_at < @older_than;"
            let! deleted = conn.ExecuteAsync(sql, {| older_than = olderThan |})
            return deleted
        }

    member _.GetRecentChatMessages(chatId: int64, since: DateTime) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT cm.user_id, cm.message_id, cm.text, cm.has_photo, cm.has_document,
       cm.reply_to_message_id, cm.created_at
FROM chat_message cm
WHERE cm.chat_id = @chat_id
  AND cm.created_at >= @since
ORDER BY cm.created_at;
"""
            let! rows = conn.QueryAsync<ChatMessageRow>(sql, {| chat_id = chatId; since = since |})
            return rows |> Seq.toArray
        }

    // ── User feedback ────────────────────────────────────────────────

    member _.SaveUserFeedback(userId: int64, feedbackText: string | null, hasMedia: bool, telegramMessageId: int) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
INSERT INTO user_feedback (user_id, feedback_text, has_media, telegram_message_id)
VALUES (@user_id, @feedback_text, @has_media, @telegram_message_id)
RETURNING id;
"""
            let! id = conn.QuerySingleAsync<int64>(sql,
                {| user_id = userId
                   feedback_text = feedbackText
                   has_media = hasMedia
                   telegram_message_id = telegramMessageId |})
            return id
        }

    member _.UpdateFeedbackGitHubIssue(feedbackId: int64, issueNumber: int) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql = "UPDATE user_feedback SET github_issue_number = @issue_number WHERE id = @id;"
            let! _ = conn.ExecuteAsync(sql, {| id = feedbackId; issue_number = issueNumber |})
            return ()
        }
