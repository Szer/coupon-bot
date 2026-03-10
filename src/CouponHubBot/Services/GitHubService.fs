namespace CouponHubBot.Services

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open Microsoft.Extensions.Logging
open CouponHubBot

type GitHubService(httpClient: HttpClient, botConfig: BotConfiguration, logger: ILogger<GitHubService>, time: TimeProvider) =

    do
        httpClient.BaseAddress <- Uri("https://api.github.com")
        httpClient.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue("application/vnd.github+json"))
        httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28")
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CouponHubBot/1.0")

        if not (String.IsNullOrWhiteSpace botConfig.GitHubToken) then
            httpClient.DefaultRequestHeaders.Authorization <-
                AuthenticationHeaderValue("Bearer", botConfig.GitHubToken)

    let repoApiUrl =
        let repo = botConfig.GitHubRepo.Trim()
        let parts = repo.Split('/', StringSplitOptions.RemoveEmptyEntries)
        if parts.Length = 2 then
            let owner = parts[0].Trim()
            let name = parts[1].Trim()
            if owner <> "" && name <> "" then
                Some $"/repos/{owner}/{name}"
            else
                logger.LogWarning("GITHUB_REPO must be in owner/repo format, got: {Repo}", botConfig.GitHubRepo)
                None
        else
            logger.LogWarning("GITHUB_REPO must be in owner/repo format, got: {Repo}", botConfig.GitHubRepo)
            None

    /// Neutralize GitHub @mentions to avoid unwanted notifications on the public repo
    let neutralizeMentions (text: string) =
        if String.IsNullOrEmpty text then text
        else text.Replace("@", "@\u200B")

    member _.IsConfigured =
        repoApiUrl.IsSome
        && not (String.IsNullOrWhiteSpace botConfig.GitHubToken)

    member _.CreateFeedbackIssue(feedbackText: string | null, hasMedia: bool) =
        task {
            match repoApiUrl with
            | None ->
                logger.LogWarning("GitHub integration not configured, skipping issue creation")
                return None
            | Some baseUrl ->
                let safeText =
                    if not (String.IsNullOrWhiteSpace feedbackText) then neutralizeMentions feedbackText
                    else feedbackText

                let titlePreview =
                    if String.IsNullOrWhiteSpace safeText then
                        if hasMedia then "media message" else "empty message"
                    else
                        let maxLen = 60
                        if safeText.Length <= maxLen then safeText
                        else safeText.Substring(0, maxLen) + "..."

                let title = $"[Feedback] {titlePreview}"

                let bodyParts = ResizeArray<string>()
                let dateStr = time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd HH:mm")
                bodyParts.Add($"**Date:** {dateStr} UTC")
                if hasMedia then bodyParts.Add("**Attachments:** media content (see Telegram)")
                bodyParts.Add("")
                if not (String.IsNullOrWhiteSpace safeText) then
                    bodyParts.Add("---")
                    bodyParts.Add("")
                    bodyParts.Add(safeText)
                else
                    bodyParts.Add("_(no text, media-only message)_")

                let body = String.Join("\n", bodyParts)

                let payload =
                    {| title = title
                       body = body
                       labels = [| "user-feedback" |] |}

                let json = JsonSerializer.Serialize(payload)
                use content = new StringContent(json, Encoding.UTF8, "application/json")
                use! response = httpClient.PostAsync($"{baseUrl}/issues", content)

                if response.IsSuccessStatusCode then
                    let! responseBody = response.Content.ReadAsStringAsync()
                    use doc = JsonDocument.Parse(responseBody)
                    let issueNumber = doc.RootElement.GetProperty("number").GetInt32()
                    logger.LogInformation("Created GitHub issue #{IssueNumber} for user feedback", issueNumber)
                    return Some issueNumber
                else
                    let! errorBody = response.Content.ReadAsStringAsync()
                    logger.LogError(
                        "Failed to create GitHub issue: {StatusCode} {Error}",
                        response.StatusCode,
                        errorBody
                    )
                    return None
        }

    member _.AssignProductAgent(issueNumber: int) =
        task {
            match repoApiUrl with
            | None -> ()
            | Some baseUrl ->
                let payload =
                    JsonSerializer.Serialize(
                        {| assignees = [| "copilot-swe-agent[bot]" |]
                           agent_assignment = {| custom_agent = "product" |} |}
                    )

                use content = new StringContent(payload, Encoding.UTF8, "application/json")
                use! response = httpClient.PostAsync($"{baseUrl}/issues/{issueNumber}/assignees", content)

                if response.IsSuccessStatusCode then
                    logger.LogInformation("Assigned product agent to issue #{IssueNumber}", issueNumber)
                else
                    let! errorBody = response.Content.ReadAsStringAsync()
                    logger.LogWarning(
                        "Failed to assign product agent to issue #{IssueNumber}: {StatusCode} {Error}",
                        issueNumber,
                        response.StatusCode,
                        errorBody
                    )
        }
