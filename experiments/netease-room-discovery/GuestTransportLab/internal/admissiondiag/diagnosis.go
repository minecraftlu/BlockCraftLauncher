// Package admissiondiag classifies only the numeric error code present in
// a real TanEnterRoomResponse. It does not infer account, privacy, server
// capacity or game identity from cached room metadata.
package admissiondiag

const (
    ReplyNotObserved = "ADMISSION_REPLY_NOT_OBSERVED"
    Accepted         = "ADMISSION_SERVER_ACCEPTED_GAME_NOT_JOINED"
    RoomNotFound     = "ADMISSION_ROOM_NOT_FOUND"
    RoomFull         = "ADMISSION_ROOM_FULL"
    WrongPasscode    = "ADMISSION_WRONG_PASSCODE"
    RejectedUnknown  = "ADMISSION_REJECTED_REASON_UNKNOWN"
)

// Error codes are int8 on the wire, as defined by the pinned
// nemc-tan-lobby-solver v0.2.8-compatible TanEnterRoomResponse.
const (
    SuccessCode  int8 = 0
    NotFoundCode int8 = 1
    FullCode     int8 = 4
    PasswordCode int8 = 14
)

// Classify never emits success without an observed server response.
// Unknown error codes deliberately remain unknown: the host's blocklist,
// account mode, room privacy and capacity cannot be inferred safely.
func Classify(replyObserved bool, code int8) string {
    if !replyObserved{return ReplyNotObserved}
    switch code {
    case SuccessCode:return Accepted
    case NotFoundCode:return RoomNotFound
    case FullCode:return RoomFull
    case PasswordCode:return WrongPasscode
    default:return RejectedUnknown
    }
}
