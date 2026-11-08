package main

import (
    "context"
    "errors"
    "path/filepath"
    "testing"
)

func TestFixtureRejectsUnsafeLaunch(t *testing.T) {
    if err:=runOffline(context.Background(),"relative-worker.exe");!errors.Is(err,errFixture) {
        t.Fatalf("relative process path unexpectedly accepted: %v",err)
    }
    missing:=filepath.Join(t.TempDir(),"missing-worker.exe")
    if err:=runOffline(context.Background(),missing);!errors.Is(err,errFixture) {
        t.Fatalf("missing worker unexpectedly accepted: %v",err)
    }
    ctx,cancel:=context.WithCancel(context.Background())
    cancel()
    if err:=runOffline(ctx,missing);!errors.Is(err,errFixture) {
        t.Fatalf("cancelled context unexpectedly launched worker: %v",err)
    }
}
