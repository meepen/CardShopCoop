using System.Text.Json;
using CardShopCoop.Sync;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition)
        throw new Exception(name);
    Console.WriteLine("PASS " + name);
    passed++;
}

PlayTableRecord Entry(int key, long revision, int phase, string match = "m", long epoch = 7) =>
    new PlayTableRecord
    {
        TableKey = key,
        MatchId = match,
        OwnerConn = 2,
        Phase = phase,
        Epoch = epoch,
        Revision = revision
    };

var model = new PlayTableStateModel();
Check(model.Apply(new PlayTableStateFrame { Epoch = 7, Tables = new() { Entry(1, 1, 1) } })
    == PlayTableApplyResult.Applied, "reserve applies");
Check(model.Apply(new PlayTableStateFrame { Epoch = 7, Tables = new() { Entry(1, 1, 1) } })
    == PlayTableApplyResult.Duplicate, "duplicate is harmless");
Check(model.Apply(new PlayTableStateFrame { Epoch = 7, Tables = new() { Entry(1, 3, 2) } })
    == PlayTableApplyResult.Applied, "started phase and revision apply");
Check(model.TryGet(1, out var current) && current.Phase == PlayTableRecord.PhaseStarted,
    "started phase is retained");
Check(model.Apply(new PlayTableStateFrame { Epoch = 7, Tables = new() { Entry(1, 2, 0, "old") } })
    == PlayTableApplyResult.Duplicate, "delayed release cannot roll back started");
Check(model.Apply(new PlayTableStateFrame { Epoch = 7, Tables = new() { Entry(1, 4, 0) } })
    == PlayTableApplyResult.Applied, "release tombstone applies");
Check(model.TryGet(1, out current) && current.IsReleased && current.Revision == 4,
    "release is retained as tombstone");

// Full omission is a deletion, but its revision must survive the deletion.
Check(model.Apply(new PlayTableStateFrame
{
    Epoch = 7,
    Full = true,
    Tables = new(),
    TableRevisions = new() { [1] = 4 }
}) == PlayTableApplyResult.Applied, "full omission removes covered tombstone");
Check(!model.TryGet(1, out _), "full omission removes table");
Check(model.Apply(new PlayTableStateFrame { Epoch = 7, Tables = new() { Entry(1, 3, 1) } })
    == PlayTableApplyResult.Duplicate && !model.TryGet(1, out _),
    "delayed partial cannot resurrect after full omission");
Check(model.Apply(new PlayTableStateFrame { Epoch = 7, Tables = new() { Entry(1, 5, 1) } })
    == PlayTableApplyResult.Applied && model.TryGet(1, out current) && current.Revision == 5,
    "newer partial can recreate a table");
Check(model.Apply(new PlayTableStateFrame { Epoch = 7, Tables = new() { Entry(1, 4, 0) } })
    == PlayTableApplyResult.Duplicate && model.TryGet(1, out current) && current.Revision == 5,
    "delayed tombstone cannot roll back active table");

// A full frame can arrive after either an active or a tombstone frame.
Check(model.Apply(new PlayTableStateFrame
{
    Epoch = 7,
    Full = true,
    Tables = new(),
    TableRevisions = new() { [1] = 6 }
}) == PlayTableApplyResult.Applied, "newer full omission removes active table");
Check(model.Apply(new PlayTableStateFrame { Epoch = 7, Tables = new() { Entry(1, 5, 0) } })
    == PlayTableApplyResult.Duplicate && !model.TryGet(1, out _),
    "delayed tombstone cannot resurrect after full omission");

// Future-epoch validation is atomic, including for destructive full frames.
Check(model.Apply(new PlayTableStateFrame
{
    Epoch = 8,
    Full = true,
    Tables = new() { Entry(2, 1, 1, epoch: 9) },
    TableRevisions = new() { [1] = 1 }
}) == PlayTableApplyResult.Invalid && model.Epoch == 7,
    "malformed future full frame does not reset state");

var futureModel = new PlayTableStateModel();
Check(futureModel.Apply(new PlayTableStateFrame
{
    Epoch = 7,
    Tables = new() { Entry(8, 2, 1) }
}) == PlayTableApplyResult.Applied, "future atomicity baseline applies");
Check(futureModel.Apply(new PlayTableStateFrame
{
    Epoch = 8,
    Full = true,
    Tables = new() { Entry(8, 3, 1, epoch: 8) },
    TableRevisions = new() { [8] = 2 }
}) == PlayTableApplyResult.Invalid && futureModel.Epoch == 7
    && futureModel.TryGet(8, out var futureBaseline) && futureBaseline.Revision == 2,
    "nonempty inconsistent future full frame is atomic");
Check(model.Apply(null) == PlayTableApplyResult.Invalid, "null frame is invalid");
Check(model.Apply(new PlayTableStateFrame { Epoch = 7, Tables = null! })
    == PlayTableApplyResult.Invalid, "null table list is invalid");
Check(model.Apply(new PlayTableStateFrame
{
    Epoch = 7,
    Tables = new() { null! }
}) == PlayTableApplyResult.Invalid, "null table entry is invalid");
Check(model.Apply(new PlayTableStateFrame
{
    Epoch = 7,
    Tables = new() { Entry(2, 0, 1) }
}) == PlayTableApplyResult.Invalid, "zero revision is invalid");
Check(model.Apply(new PlayTableStateFrame
{
    Epoch = 7,
    Full = true,
    Tables = new() { Entry(9, 1, 1), Entry(9, 1, 2, "conflict") },
    TableRevisions = new() { [9] = 1 }
}) == PlayTableApplyResult.Invalid, "duplicate revision conflict is atomic");
Check(model.Apply(new PlayTableStateFrame
{
    Epoch = 7,
    Full = true,
    Tables = new(),
    TableRevisions = new()
}) == PlayTableApplyResult.Invalid, "full frame cannot omit known watermarks");

Check(model.Apply(new PlayTableStateFrame { Epoch = 7, Tables = new() { Entry(9, 1, 1) } })
    == PlayTableApplyResult.Applied, "clone isolation baseline applies");
Check(model.Apply(new PlayTableStateFrame
{
    Epoch = 7,
    Full = true,
    Tables = new() { Entry(9, 2, 1) },
    TableRevisions = new() { [9] = 3 }
}) == PlayTableApplyResult.Invalid && model.TryGet(9, out var inconsistentBaseline)
    && inconsistentBaseline.Revision == 1,
    "full present revision below watermark is rejected atomically");
// Records returned by the model are snapshots, not writable internal state.
Check(model.TryGet(1, out _) == false, "omitted table remains absent");
var snapshot = model.Tables;
Check(snapshot.Count == 1 && snapshot[9].Revision == 1, "table snapshot contains baseline");
snapshot[9].Revision = 99;
Check(model.TryGet(9, out current) && current.Revision == 1,
    "mutating Tables record cannot mutate model");
current.Revision = 88;
Check(model.TryGet(9, out var tryGetSnapshot) && tryGetSnapshot.Revision == 1,
    "mutating TryGet record cannot mutate model");
((IDictionary<int, PlayTableRecord>)snapshot).Clear();
Check(model.Tables.Count == 1, "table dictionary cannot mutate model");

// Verify the wire-shaped foundation types survive field-based JSON serialization.
var jsonOptions = new JsonSerializerOptions { IncludeFields = true };
var serialized = JsonSerializer.Serialize(new PlayTableStateFrame
{
    Epoch = 11,
    Full = true,
    Tables = new() { Entry(3, 2, PlayTableRecord.PhaseStarted, epoch: 11) },
    TableRevisions = new() { [3] = 2 }
}, jsonOptions);
var decoded = JsonSerializer.Deserialize<PlayTableStateFrame>(serialized, jsonOptions);
var roundTrip = new PlayTableStateModel();
Check(decoded != null && roundTrip.Apply(decoded) == PlayTableApplyResult.Applied
    && roundTrip.TryGet(3, out current) && current.Phase == PlayTableRecord.PhaseStarted,
    "state frame serializes and restores started phase");

Console.WriteLine($"{passed} reducer checks passed.");

var wireMessage = new PlayTableMatchState
{
    Epoch = 12,
    Full = true,
    Matches = new() { new PlayTableMatchEntry
    {
        MatchId = "wire-match",
        Epoch = 12,
        Revision = 4,
        OwnerConn = 3,
        TableKey = 9,
        Phase = PlayTableMatchEntry.StateStarted,
    } },
    TableRevisions = new() { [9] = 4 },
};
var wirePayload = WireCodec.Serialize(wireMessage);
var decodedWire = (PlayTableMatchState)WireCodec.Deserialize(
    typeof(PlayTableMatchState), wirePayload, 0, wirePayload.Length);
Check(decodedWire.Matches.Count == 1 && decodedWire.Matches[0].Phase == PlayTableMatchEntry.StateStarted
    && decodedWire.TableRevisions[9] == 4,
    "production WireCodec round-trips started play-table DTO");
var replay = new PlayTableRequestReplayGuard();
Check(replay.TryAccept(2, 1), "first request sequence is accepted");
Check(!replay.TryAccept(2, 1) && !replay.TryAccept(2, 0), "duplicate and zero sequences are rejected");
Check(replay.TryAccept(2, 2), "newer request remains accepted after delayed old request");
Check(replay.TryAccept(2, 3) && replay.TryAccept(3, 1), "owners have independent exact sequences");
Check(replay.TryAccept(2, 4), "generated match-id collision does not reject valid request");
replay.RemoveOwner(2);
Check(replay.TryAccept(2, 1), "owner sequence resets on disconnect");
for (int owner = 4; owner <= PlayTableRequestReplayGuard.MaxOwners + 1; owner++)
    Check(replay.TryAccept(owner, 1), "bounded replay owner slot is accepted");
Check(!replay.TryAccept(PlayTableRequestReplayGuard.MaxOwners + 2, 1), "replay owner state is bounded");
Console.WriteLine($"{passed} total checks passed.");
