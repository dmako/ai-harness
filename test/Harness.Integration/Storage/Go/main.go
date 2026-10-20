// Test-only bridge to the unmodified, pinned Go storage package.
package main

import (
    "context"
    "fmt"
    "os"

    "harness/internal/storage"
)

func main() {
    db, err := storage.Open(context.Background(), os.Args[2], storage.Options{})
    if err != nil {
        panic(err)
    }
    defer db.Close()
    switch os.Args[1] {
    case "put":
        if err := db.PutDiagnostic(context.Background(), os.Args[3], os.Args[4]); err != nil {
            panic(err)
        }
    case "get":
        value, err := db.GetDiagnostic(context.Background(), os.Args[3])
        if err != nil {
            panic(err)
        }
        fmt.Println(value)
    default:
        panic("unknown storage oracle operation")
    }
}
