// Test bridge only: calls the original handler and wire helpers without changing them.
package server

import (
    "context"
    "encoding/json"
    "harness/internal/providers"
)

func PortContext(ctx context.Context, root string, registry *providers.Registry, raw json.RawMessage) (json.RawMessage, error) {
    var input contextInput
    if err := json.Unmarshal(raw, &input); err != nil { return nil, err }
    s := &Server{opts:Options{ProjectRoot:root, StateRoot:root, Registry:registry}}
    _, out, err := s.getContext(ctx, nil, input)
    if err != nil { return nil, err }
    return json.Marshal(out)
}
