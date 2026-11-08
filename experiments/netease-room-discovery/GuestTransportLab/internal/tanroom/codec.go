// Package tanroom implements independent, bounded wire-format fixtures for the
// NetEase local-lobby join messages documented by community protocol research.
// It is intentionally network-free; it does not authenticate or join rooms.
package tanroom

import (
	"bytes"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"strconv"
	"unicode/utf8"
)

const (
	EnterRoomID   uint16 = 3
	ServerReadyID uint16 = 7
	MaxWireBytes         = 256 * 1024
)

const (
	Accepted       byte = 0
	RoomNotFound   byte = 1
	RoomFull       byte = 4
	WrongPassword  byte = 14
)

type EnterRequest struct {
	OwnerID uint32
	RoomID uint32
	Password string
	EnterTeamID uint64
	EnterToken uint32
	FollowTeamID uint64
	GuestNetherNetID string
	Compressed bool
}

type EnterReceipt struct {
	ResultCode byte
	PlayerIDs []uint32
	ItemIDs []uint64
}

type ServerReady struct {
	ServerAddress string
	ServerRaknetGUID string
	RTCRoomID string
	HostNetherNetID string
	Compressed bool
}

func (r EnterReceipt) Status() string {
	switch r.ResultCode {
	case Accepted: return "ADMISSION_ACCEPTED"
	case RoomNotFound: return "ROOM_NOT_FOUND"
	case RoomFull: return "ROOM_FULL"
	case WrongPassword: return "WRONG_PASSWORD"
	default: return fmt.Sprintf("ADMISSION_REJECTED_CODE_%d", r.ResultCode)
	}
}

func EncodeEnterRequest(req EnterRequest) ([]byte, error) {
	if req.OwnerID == 0 || req.RoomID == 0 {
		return nil, errors.New("owner and room IDs must be nonzero")
	}
	if !positiveDecimal(req.GuestNetherNetID) {
		return nil, errors.New("guest network ID must be a positive decimal integer")
	}
	if len(req.Password) > 4096 {
		return nil, errors.New("password exceeds fixture size limit")
	}
	var buf bytes.Buffer
	write := func(x any) error { return binary.Write(&buf, binary.BigEndian, x) }
	if err := write(EnterRoomID); err != nil { return nil, err }
	if err := write(req.OwnerID); err != nil { return nil, err }
	if err := write(req.RoomID); err != nil { return nil, err }
	if err := writeUTF(&buf, req.Password); err != nil { return nil, err }
	if err := write(req.EnterTeamID); err != nil { return nil, err }
	if err := write(req.EnterToken); err != nil { return nil, err }
	if err := write(req.FollowTeamID); err != nil { return nil, err }
	if err := writeUTF(&buf, req.GuestNetherNetID); err != nil { return nil, err }
	if req.Compressed { buf.WriteByte(1) } else { buf.WriteByte(0) }
	return buf.Bytes(), nil
}

func DecodeEnterRequest(data []byte) (EnterRequest, error) {
	var r EnterRequest
	src, err := source(data, EnterRoomID)
	if err != nil { return r, err }
	if err := binary.Read(src, binary.BigEndian, &r.OwnerID); err != nil { return r, err }
	if err := binary.Read(src, binary.BigEndian, &r.RoomID); err != nil { return r, err }
	if r.Password, err = readUTF(src); err != nil { return r, err }
	if err := binary.Read(src, binary.BigEndian, &r.EnterTeamID); err != nil { return r, err }
	if err := binary.Read(src, binary.BigEndian, &r.EnterToken); err != nil { return r, err }
	if err := binary.Read(src, binary.BigEndian, &r.FollowTeamID); err != nil { return r, err }
	if r.GuestNetherNetID, err = readUTF(src); err != nil { return r, err }
	var flag byte
	if flag, err = src.ReadByte(); err != nil { return r, err }
	if flag > 1 { return r, errors.New("invalid compression bool") }
	r.Compressed = flag == 1
	if src.Len() != 0 { return r, errors.New("trailing bytes in enter request") }
	if r.OwnerID == 0 || r.RoomID == 0 || !positiveDecimal(r.GuestNetherNetID) {
		return r, errors.New("invalid request identifiers")
	}
	return r, nil
}

func DecodeEnterReceipt(data []byte) (EnterReceipt, error) {
	var result EnterReceipt
	src, err := source(data, EnterRoomID)
	if err != nil { return result, err }
	if result.ResultCode, err = src.ReadByte(); err != nil { return result, err }
	if result.ResultCode == Accepted {
		count, err := src.ReadByte()
		if err != nil { return result, err }
		if src.Len() < int(count)*4+1 { return result, io.ErrUnexpectedEOF }
		result.PlayerIDs = make([]uint32, int(count))
		for i := range result.PlayerIDs {
			if err = binary.Read(src, binary.BigEndian, &result.PlayerIDs[i]); err != nil { return result, err }
		}
		items, err := src.ReadByte()
		if err != nil { return result, err }
		if src.Len() < int(items)*8 { return result, io.ErrUnexpectedEOF }
		result.ItemIDs = make([]uint64, int(items))
		for i := range result.ItemIDs {
			if err = binary.Read(src, binary.BigEndian, &result.ItemIDs[i]); err != nil { return result, err }
		}
	}
	if src.Len() != 0 { return result, errors.New("unexpected trailing receipt bytes") }
	return result, nil
}

// EncodeEnterReceiptFixture is ONLY for local synthetic protocol tests.
func EncodeEnterReceiptFixture(r EnterReceipt) ([]byte, error) {
	if len(r.PlayerIDs) > 255 || len(r.ItemIDs) > 255 { return nil, errors.New("too many fixture entries") }
	var b bytes.Buffer
	binary.Write(&b, binary.BigEndian, EnterRoomID)
	b.WriteByte(r.ResultCode)
	if r.ResultCode == Accepted {
		b.WriteByte(byte(len(r.PlayerIDs)))
		for _, v := range r.PlayerIDs { binary.Write(&b, binary.BigEndian, v) }
		b.WriteByte(byte(len(r.ItemIDs)))
		for _, v := range r.ItemIDs { binary.Write(&b, binary.BigEndian, v) }
	}
	return b.Bytes(), nil
}

func DecodeServerReady(data []byte) (ServerReady, error) {
	var r ServerReady
	src, err := source(data, ServerReadyID)
	if err != nil { return r, err }
	if r.ServerAddress, err = readUTF(src); err != nil { return r, err }
	if r.ServerRaknetGUID, err = readUTF(src); err != nil { return r, err }
	if r.RTCRoomID, err = readUTF(src); err != nil { return r, err }
	if r.HostNetherNetID, err = readUTF(src); err != nil { return r, err }
	var flag byte
	if flag, err = src.ReadByte(); err != nil { return r, err }
	if flag > 1 { return r, errors.New("invalid ready compression flag") }
	r.Compressed = flag == 1
	if src.Len() != 0 { return r, errors.New("unexpected trailing ready bytes") }
	if !positiveDecimal(r.HostNetherNetID) {
		return r, errors.New("server ready has invalid host network ID")
	}
	return r, nil
}

// EncodeServerReadyFixture is ONLY for local synthetic protocol tests.
func EncodeServerReadyFixture(r ServerReady) ([]byte, error) {
	if !positiveDecimal(r.HostNetherNetID) { return nil, errors.New("invalid mock host ID") }
	var b bytes.Buffer
	binary.Write(&b, binary.BigEndian, ServerReadyID)
	for _, v := range []string{r.ServerAddress, r.ServerRaknetGUID, r.RTCRoomID, r.HostNetherNetID} {
		if err := writeUTF(&b, v); err != nil { return nil, err }
	}
	if r.Compressed { b.WriteByte(1) } else { b.WriteByte(0) }
	return b.Bytes(), nil
}

func source(data []byte, id uint16) (*bytes.Reader, error) {
	if len(data) < 2 || len(data) > MaxWireBytes { return nil, errors.New("invalid packet size") }
	r := bytes.NewReader(data)
	var received uint16
	if err := binary.Read(r, binary.BigEndian, &received); err != nil { return nil, err }
	if received != id { return nil, fmt.Errorf("unexpected packet ID %d", received) }
	return r, nil
}

func writeUTF(dst *bytes.Buffer, s string) error {
	if !utf8.ValidString(s) || len(s) > 65535 { return errors.New("invalid or oversized UTF-8 string") }
	if err := binary.Write(dst, binary.BigEndian, uint16(len(s))); err != nil { return err }
	_, err := dst.WriteString(s)
	return err
}

func readUTF(src *bytes.Reader) (string, error) {
	var size uint16
	if err := binary.Read(src, binary.BigEndian, &size); err != nil { return "", err }
	if int(size) > src.Len() { return "", io.ErrUnexpectedEOF }
	b := make([]byte, int(size))
	if _, err := io.ReadFull(src, b); err != nil { return "", err }
	if !utf8.Valid(b) { return "", errors.New("invalid UTF-8 field") }
	return string(b), nil
}

func positiveDecimal(s string) bool {
	n, err := strconv.ParseUint(s, 10, 64)
	return err == nil && n != 0
}
