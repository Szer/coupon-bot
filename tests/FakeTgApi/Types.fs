namespace FakeTgApi

open System

[<CLIMutable>]
type ApiCallLog =
    { Method: string
      Url: string
      Body: string
      Timestamp: DateTime }

[<CLIMutable>]
type ChatMemberMock =
    { userId: int64
      status: string } // "member" | "left" | "kicked" | "administrator"

