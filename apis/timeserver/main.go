package main

import (
	"fmt"
	"log"
	"net/http"
	"time"
)

func main() {

	http.HandleFunc("/time", func(w http.ResponseWriter, r *http.Request) {
		fmt.Fprintf(w, "Current time is: %s\n", time.Now().Format(time.RFC822))
	})

	log.Fatal(http.ListenAndServe(":8000", nil))
}
