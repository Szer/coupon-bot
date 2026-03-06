namespace CouponHubBot.Services

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open Microsoft.Extensions.Logging
open CouponHubBot

type GitHubService(httpClient: HttpClient, gitHubConfig: GitHubConfiguration, logger: ILogger<GitHubService>) =

    do
        httpClient.BaseAddress <- Uri("https://api.github.com")
        httpClient.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue("application/vnd.github+json"))
        httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28")
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CouponHubBot/1.0")

        if not (String.IsNullOrWhiteSpace gitHubConfig.Token) then
            httpClient.DefaultRequestHeaders.Authorization <-
                AuthenticationHeaderValue("Bearer", gitHubConfig.Token)

    let isConfigured =
        not (String.IsNullOrWhiteSpace gitHubConfig.Token)
        && not (String.IsNullOrWhiteSpace gitHubConfig.Repo)

    let repoApiUrl =
        if isConfigured then
            let parts = gitHubConfig.Repo.Split('/')
            if parts.Length = 2 then
                Some $"/repos/{parts[0]}/{parts[1]}"
            else
                logger.LogWarning("GITHUB_REPO must be in owner/repo format, got: {Repo}", gitHubConfig.Repo)
                None
        else
            None

    member _.IsConfigured = isConfigured && repoApiUrl.IsSome

    member _.CreateFeedbackIssue(userDisplayName: string, feedbackText: string, hasMedia: bool) =
        task {
            match repoApiUrl with
            | None ->
                logger.LogWarning("GitHub integration not configured, skipping issue creation")
                return None
            | Some baseUrl ->
                let titlePreview =
                    if String.IsNullOrWhiteSpace feedbackText then
                        if hasMedia then "media message" else "empty message"
                    else
                        let maxLen = 60
                        if feedbackText.Length <= maxLen then feedbackText
                        else feedbackText.Substring(0, maxLen) + "..."

                let title = $"[Feedback] @{userDisplayName}: {titlePreview}"

                let bodyParts = ResizeArray<string>()
                bodyParts.Add($"**From:** @{userDisplayName}")
                let dateStr = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")
                bodyParts.Add($"**Date:** {dateStr} UTC")
                if hasMedia then bodyParts.Add("**Attachments:** media content (see Telegram)")
                bodyParts.Add("")
                if not (String.IsNullOrWhiteSpace feedbackText) then
                    bodyParts.Add("---")
                    bodyParts.Add("")
                    bodyParts.Add(feedbackText)
                else
                    bodyParts.Add("_(no text, media-only message)_")

                let body = String.Join("\n", bodyParts)

                let payload =
                    {| title = title
                       body = body
                       labels = [| "user-feedback" |] |}

                let json = JsonSerializer.Serialize(payload)
                use content = new StringContent(json, Encoding.UTF8, "application/json")
                let! response = httpClient.PostAsync($"{baseUrl}/issues", content)

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
                // Assign copilot-swe-agent[bot] with product custom agent
                let payload =
                    JsonSerializer.Serialize(
                        {| assignees = [| "copilot-swe-agent[bot]" |]
                           agent_assignment = {| custom_agent = "product" |} |}
                    )

                use content = new StringContent(payload, Encoding.UTF8, "application/json")
                let! response = httpClient.PostAsync($"{baseUrl}/issues/{issueNumber}/assignees", content)

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
