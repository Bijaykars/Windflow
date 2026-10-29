# Revit live link protocol

A named pipe the WindFlow Revit add-in uses to keep the standalone WindFlow.App in sync with the
active Revit document while the user is editing. The App side is `src/WindFlow.App/LiveLink.cs`
(server) and `MainWindowLiveLink.cs` (applying messages); it is started with `--livelink`. `tools/livelink-fake-revit.ps1` is a client stand-in, `tools/livelink-fake-app.ps1`
a server stand-in.

## Transport

- Pipe name: `\\.\pipe\WindFlow.LiveLink`, local machine and current user only. The server creates the pipe with
  `PIPE_REJECT_REMOTE_CLIENTS` (the SMB path `\\127.0.0.1\pipe\...` is refused) and a user-only DACL (the NETWORK SID is denied first,
  then only the current user is allowed); the Revit client opens it with `PipeOptions.CurrentUserOnly`, which checks that the
  server's owner is the current user (batch 3a, S4-05).
- **The app is the server, Revit is the client.** The app creates the pipe once at startup (or on
  demand) and accepts one connection; Revit's add-in connects to it.
- Messages are **newline-delimited JSON**, one complete JSON object per line (`\n` terminated, UTF-8,
  no embedded newlines inside a message — the `json` field of a `model` message is itself a JSON
  string, so any newlines inside the serialized `BuildingModel` are already escaped by the JSON
  encoder that produced it).
- No message is larger than necessary, but there is no hard size cap in the protocol: a `model`
  message embeds a full `BuildingModel.ToJson()` and can be several MB for a large building. Readers
  must use a streaming/unbounded line reader (e.g. `StreamReader.ReadLine`), not a fixed-size buffer.
  The app accepts lines up to 64 MB; a longer line is dropped and acked with an error.

## Messages: Revit -> App

- **`model`** — sent once on connect, and again after any change that affects geometry, openings,
  rooms, or air terminals, debounced to at most once per second of quiet editing.

  ```json
  {"type":"model","doc":"<document name>","json":"<BuildingModel.ToJson(), escaped as a JSON string>"}
  ```

  Every `model` message carries the **entire** building, not a delta. This is deliberately simple:
  the App can always apply a `model` message by discarding whatever it had and running the normal
  "load a `.windflow.json`" path. Sending only the changed region is a later optimisation once this
  is proven to be too slow for large buildings or too chatty over the pipe — not built here.

- **`opening`** — a cheap open/close toggle for a single door or window, sent instead of a full
  `model` when the add-in can positively identify that the only thing that changed was that one
  element's open/closed state (an integer yes/no instance parameter whose name contains "open").
  Any change the add-in can't identify this narrowly falls back to a full `model` message.

  ```json
  {"type":"opening","id":"<Opening.Id, i.e. the Revit Element.UniqueId>","isOpen":true}
  ```

- **`ping`** — a liveness heartbeat, sent every 5 seconds regardless of other traffic, so the app
  can detect a silently-stalled pipe (as opposed to a cleanly closed one) and so a dumb pipe-sniffer
  has something to see even on an idle document.

  ```json
  {"type":"ping"}
  ```

- **`close`** — sent once when the linked Revit document is closed (`DocumentClosing`), immediately
  before the add-in tears the connection down. Lets the app show "disconnected" rather than silently
  going stale.

  ```json
  {"type":"close"}
  ```

## Messages: App -> Revit

- **`ack`** — acknowledges a message by sequence number, for the app's own bookkeeping (e.g. "the
  model I'm currently showing is as of seq N"). Revit does not require or wait for acks; they exist
  so the app can reason about how far behind it is if it ever needs to.

  ```json
  {"type":"ack","seq":42}
  {"type":"ack","seq":43,"error":"model has no geometry (no obstacles)"}
  ```

  `seq` counts the messages received on the current connection, starting at 1 (Revit messages carry no
  sequence number of their own). A message the app could not apply — malformed JSON, an unknown type, a
  model that fails validation, an `opening` id not in the current model — is logged, left unapplied, and
  acked with an `error` field; the connection stays up.

- **`select`** *(optional)* — asks Revit to select a set of elements, for highlighting something the
  user clicked on in the app's viewport back in the Revit UI. Ids are Revit `UniqueId` strings (the
  same ids used everywhere else in the protocol and in `BuildingModel`/`Opening.Id`).

  ```json
  {"type":"select","ids":["3f2a1c9e-...-0001b2c3"]}
  ```

  Revit applies this via `Selection.SetElementIds`, on its own API thread (an `ExternalEvent`); any id
  that no longer resolves in the current document is silently skipped.

## Reconnect rules

- While the "Live link" toggle is on, the add-in retries the pipe connection every **5 seconds**
  forever (no backoff, no retry limit) — the expected failure mode is "the user hasn't launched
  WindFlow.App yet" or "the app briefly restarted", both of which resolve themselves.
- The ribbon button reflects state as its label: `Live link (off)` / `Live link (connecting)` /
  `Live link (waiting for WindFlow)` while retrying / `Live link (connected)` once the pipe accepts.
- On every fresh connect (including a reconnect after a drop), the add-in immediately (re)sends a
  full `model` message — the app must never assume a reconnect picks up where the last session left
  off; treat every connect as "here is the current truth".
- Turning the toggle off, or closing the document, sends `close` (if still connected) and stops
  retrying; turning it back on starts a brand-new connection cycle.

## Applying a `model` message (app side)

The app **must** apply an incoming `model` message through its normal command path — the same code
path used when opening a `.windflow.json` file from disk (deserialize with
`WindFlow.Core.BuildingModel.FromJson`, then hand it to whatever the app already does with a freshly
opened model: geometry rebuild, solver reconfigure, etc.). There is no separate "live update" code
path in the protocol; `model` is just that file's contents pushed over a pipe instead of read from
disk. This keeps the live link additive — nothing the app already does with a `BuildingModel` needs
to change to support it.

What the app does differently from a file open: the camera is kept (after the first model), devices that
were added in the app (ids `dev-*`) are carried onto the next Revit model when no Revit element has the
same id, and a running solver restarts on the new model. Holds, Compare, the box and the device selection
are reset exactly as for a file open. An `opening` message goes through the same path as clicking the
opening (a warm geometry update, not a restart), and is a no-op when the state already matches. `close`
leaves the model on screen and the status strip reads "Revit closed the document".

## Known limitations (deliberately out of scope here)

- Full model on every change, no deltas (see above).
- No compression — for a very large building this is plain JSON text over a local pipe; acceptable
  for the debounce interval used, revisit if that stops being true.
- No encryption, and no authentication beyond the operating system's: since batch 3a the pipe refuses remote clients
  (`PIPE_REJECT_REMOTE_CLIENTS`) and carries a user-only DACL, and the client verifies the server's owner
  (`PipeOptions.CurrentUserOnly`). Any process running as the same user can still connect. One message line is capped at 64 MB.
- The `opening` heuristic (an integer instance parameter whose name contains "open") is best-effort;
  families that model open/closed some other way (e.g. an angle parameter, a type swap) always fall
  back to a full `model` message, which is correct but not cheap.
