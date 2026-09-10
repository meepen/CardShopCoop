using System;
using System.Collections.Generic;
using CardShopCoop.Net.Messages;
using UnityEngine;

namespace CardShopCoop
{
    public partial class CoopCore
    {
        private bool _gradedSent;
        private float _gradedTimer;
        /// <summary>-1, not 0, so a guest whose graded album is genuinely EMPTY still sends its
        /// first (empty) digest instead of matching a zero-initialised hash and reporting nothing
        /// for the whole session.</summary>
        private int _lastGradedHash = -1;
        /// <summary>Peers we have EVER put a graded-drift alert ON SCREEN for. Read only by
        /// GradedAlertMode.OncePerSession; nothing but a new world or a dead session clears it.
        /// Separate from <see cref="_gradedAlertStanding"/> on purpose: "has this player ever
        /// been told" and "is an alarm currently up" answer different questions, and OncePerSession
        /// needs the first to survive the retraction that clears the second.</summary>
        private readonly HashSet<int> _gradedAlertEverShown = new HashSet<int>();
        /// <summary>Peers whose graded-drift alert is currently STANDING - put on screen and not
        /// yet retracted. THE ONLY LATCH THE ALL-CLEAR READS, and the whole retraction contract:
        /// it is set only inside the host's showAlert branch and cleared only when a later digest
        /// comes back identical, so an all-clear can never fire for an alarm the config
        /// suppressed, and a player who never saw the alarm never gets one. Nothing else touches
        /// it - notably a press of the adopt button does NOT, because a press does not prove the
        /// albums now match.</summary>
        private readonly HashSet<int> _gradedAlertStanding = new HashSet<int>();
        /// <summary>Per peer, the graded cards THEY have that WE do not - the adopt candidates.
        /// Populated on both roles: the host builds it from the guest's digest, and the guest
        /// builds it from the digest the host sends back when it finds a divergence.</summary>
        private readonly Dictionary<int, List<Util.GradingInterop.GradedEntry>> _gradedPeerOnly =
            new Dictionary<int, List<Util.GradingInterop.GradedEntry>>();

        /// <summary>One offer row for the F2 panel. Rebuilt on the main thread whenever the diff
        /// changes; CoopUI latches the list in its Layout pass (IMGUI matches Layout to Repaint by
        /// control index, so a row count that changes mid-frame throws over the whole window).</summary>
        public struct GradedAdoptOffer
        {
            public int ConnId; public string Who; public int Count;
        }
        public readonly List<GradedAdoptOffer> GradedAdoptOffers = new List<GradedAdoptOffer>();

        private static int GradedHash(List<Util.GradingInterop.GradedEntry> inv)
        {
            // Order-INdependent: the album list shifts on every RemoveAt, and an order-sensitive
            // hash would re-send the identical set every time a card moved position.
            int h = inv.Count;
            for (int i = 0; i < inv.Count; i++)
                h ^= inv[i].Key.GetHashCode();
            return h;
        }

        private void SendGradedDigest(int connId, List<Util.GradingInterop.GradedEntry> inv)
        {
            // "Cannot check now" never goes on the wire - a null union is a world still loading,
            // and the peer would read the short list as our real album (see
            // GradingInterop.BuildGradedCertInventory). Both callers already skip on null; this
            // is the backstop that keeps a future third caller from shipping a partial view.
            if (inv == null)
                return;
            try
            {
                var message = new GradedDigestMessage();
                for (int i = 0; i < inv.Count; i++)
                {
                    var e = inv[i];
                    message.Entries.Add(new GradedDigestEntry
                    {
                        Expansion = e.Expansion,
                        Monster = e.Monster,
                        Border = e.Border,
                        IsFoil = e.IsFoil,
                        IsDestiny = e.IsDestiny,
                        Encoded = e.Encoded,
                    });
                }
                Send(connId, message);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("graded digest: " + e.Message); }
        }

        // Graded-digest and catalog-digest wire layouts are owned by their DTOs
        // (GradedDigestMessage / CatalogDigestMessage) in Net\Messages.

        /// <summary>One entry as a report line. <paramref name="full"/> swaps in the complete
        /// save-index identity for the lines that assert two entries are DIFFERENT cards - see
        /// <see cref="CardIdentFull"/>; everywhere else the short name keeps the summaries
        /// readable.</summary>
        private static string GradedDesc(Util.GradingInterop.GradedEntry e, bool full = false)
        {
            int company, cert;
            Util.GradingInterop.DecodeCert(e.Encoded, out company, out cert);
            string s = (full ? CardIdentFull(e) : CardIdent(e.ToCard())) + " grade " + Util.GradingInterop.Actual(e.Encoded);
            if (cert > 0)
                s += " (cert " + cert + ")";
            if (Util.GradingInterop.CheatFlagged(e.Encoded))
                s += " [FAKE-flagged]";
            return s;
        }

        /// <summary>Card id for the two lines that have to say WHY <see cref="SameCard"/> said no.
        /// CardIdent alone names only SOME of the identity, so a divergence in the rest rendered as
        /// the SAME STRING TWICE - the field log read "cert 4050 is AscendedHeroesEXP#411 here and
        /// AscendedHeroesEXP#411 on rocio", i.e. a collision reported against what it itself names
        /// as one card. This spells out all five fields <see cref="SameCard"/> compares, so a line
        /// that says DIFFERENT can always be checked against a line that says which field differed.
        ///
        /// The expansion is printed HERE rather than left to CardIdent, and that is the whole point
        /// of this method rather than an accident: CardIdent only carries the expansion on two of
        /// its three arms - a modded id renders "Expansion#N" and an unresolved one renders
        /// "unknown-pack card #N", but every VANILLA expansion falls into
        /// <c>(int)expansionType &lt; (int)ECardExpansionType.MAX</c> and returns the bare
        /// <c>monsterType.ToString()</c> with no expansion at all (CoopCore.cs:1568). SameCard does
        /// compare Expansion, so without this prefix two vanilla sets sharing a monster ordinal
        /// were exactly the illegible repeat this method exists to kill. Naming it unconditionally
        /// also means CardIdent can keep changing its own branching without silently re-opening
        /// that hole; the cost is that a modded id repeats its expansion, which is noise in a
        /// diagnostic line and cheap next to another unreadable field report.
        ///
        /// Border and foil are the rest of what CPlayerData.GetCardSaveIndex consumes
        /// (decompiled/CPlayerData.cs:795-811) and isDestiny is the one identity field SameCard
        /// checks from outside it. Reached by the CERT COLLISION line and, through
        /// <c>GradedDesc(e, full: true)</c>, by the adopt's DIFFERENT-card refusal; every other
        /// report line is about a card rather than a disagreement over one and keeps the short
        /// name.</summary>
        private static string CardIdentFull(Util.GradingInterop.GradedEntry e)
        {
            return e.Expansion + " " + CardIdent(e.ToCard()) + " " + e.Border
                + (e.IsFoil ? " foil" : "") + (e.IsDestiny ? " destiny" : "");
        }

        /// <summary>Same CARD, ignoring the grade - every field that feeds
        /// CPlayerData.GetCardSaveIndex (monster, border, foil) plus expansion and isDestiny.
        /// Border and foil are in here on purpose: two border variants of one monster are two
        /// different save slots, so calling them "the same card" would file a cert clash as a
        /// harmless re-encoding.</summary>
        private static bool SameCard(Util.GradingInterop.GradedEntry a, Util.GradingInterop.GradedEntry b)
        {
            return a.Expansion == b.Expansion && a.Monster == b.Monster && a.Border == b.Border
                && a.IsFoil == b.IsFoil && a.IsDestiny == b.IsDestiny;
        }

        /// <summary>Set difference between the peer's graded-cert union and ours. Report only -
        /// it never adds, removes or rewrites a card. <paramref name="isHost"/> owns the two
        /// things only a host may do: toast the sender, and answer EVERY digest with our own so
        /// the guest can see ITS one-sided half and can retract a stale offer (the guest must
        /// never answer back, or the two would ping-pong digests forever); only a divergence
        /// toasts.
        ///
        /// TWO SETS, TWO JOBS - do not collapse them back into one. `mineKeys` is the NARROW
        /// walk, because it is compared against a peer that can only see the mirrored containers
        /// and because the same list is what SendGradedDigest puts on the wire; `mineByCert` is
        /// the WIDE union (narrow + GradingInterop.LocalOnlyGradedCerts), because every question
        /// it answers is "does Grading Overhaul already see this cert on this PC?" and GO counts
        /// the hand. Widening mineByCert can only ever move a card OUT of the adopt offer and
        /// into a report count - peerOnlyTotal is still gated on the narrow mineKeys - so it
        /// cannot inflate the divergence.</summary>
        private void CompareGradedDigests(GradedDigestMessage message, int connId, bool isHost)
        {
            var theirs = new List<Util.GradingInterop.GradedEntry>(message.Entries.Count);
            for (int i = 0; i < message.Entries.Count; i++)
            {
                var e = message.Entries[i];
                theirs.Add(new Util.GradingInterop.GradedEntry
                {
                    Expansion = e.Expansion,
                    Monster = e.Monster,
                    Border = e.Border,
                    IsFoil = e.IsFoil,
                    IsDestiny = e.IsDestiny,
                    Encoded = e.Encoded,
                });
            }
            var mine = Util.GradingInterop.BuildGradedCertInventory();
            // FAIL CLOSED DURING A LOAD. Null is "cannot check now", never "my album is empty":
            // the live containers fill progressively while the world spawns, and a short union
            // here reports the peer's whole shelf as one-sided and offers it for adoption. Skip
            // the cycle entirely - the digest timer brings us straight back.
            if (mine == null)
            {
                CoopPlugin.Log.LogInfo("graded album check: skipped - the world is still loading here, so this PC cannot see its own shelves yet");
                return;
            }

            var mineKeys = new HashSet<string>();
            var mineByCert = new Dictionary<long, Util.GradingInterop.GradedEntry>();
            for (int i = 0; i < mine.Count; i++)
            {
                mineKeys.Add(mine[i].Key);
                long ck = Util.GradingInterop.CertKey(mine[i].Encoded);
                if (ck != 0L && !mineByCert.ContainsKey(ck))
                    mineByCert[ck] = mine[i];
            }
            // The local-only half of the GUARD set, mirrored containers first so a collision
            // report names the card the peer could actually be told about.
            var localOnly = Util.GradingInterop.LocalOnlyGradedCerts();
            for (int i = 0; i < localOnly.Count; i++)
            {
                long ck = Util.GradingInterop.CertKey(localOnly[i].Encoded);
                if (ck != 0L && !mineByCert.ContainsKey(ck))
                    mineByCert[ck] = localOnly[i];
            }

            string who = PeerNames.TryGetValue(connId, out var nm) ? nm : (isHost ? "the joiner" : "the host");

            var peerOnly = new List<Util.GradingInterop.GradedEntry>();
            var peerExamples = new List<string>();
            var collisions = new List<string>();
            var reEncodings = new List<string>();
            // Certs the two PCs agree on the CARD for but disagree on the ENCODING of. Both
            // halves are excluded from the one-sided counts below - they are ONE card in two
            // states, and printing them as "only here" plus "only on theirs" is what made the
            // field report read as two unrelated missing cards.
            var reEncodedCerts = new HashSet<long>();
            var theirKeys = new HashSet<string>();
            int peerOnlyTotal = 0, peerOnlyNoContent = 0, collisionTotal = 0;
            int reEncodedTotal = 0, peerOnlyCertHeld = 0;
            for (int i = 0; i < theirs.Count; i++)
            {
                var e = theirs[i];
                theirKeys.Add(e.Key);

                // Does a card carrying THIS cert already live somewhere on this PC - anywhere
                // Grading Overhaul can see it, hand and submit scratch set included? Decided
                // once, up front, because it gates both the report category and adopt candidacy,
                // and it reads the WIDE mineByCert for the reason given on this method.
                long theirCk = Util.GradingInterop.CertKey(e.Encoded);
                Util.GradingInterop.GradedEntry m = default(Util.GradingInterop.GradedEntry);
                bool certHeldHere = theirCk != 0L && mineByCert.TryGetValue(theirCk, out m);
                bool sameCard = certHeldHere && SameCard(m, e);
                // Same cert, same card, DIFFERENT encoding - the field symptom (host 1380002639
                // vs wire 380002639: identical company/grade/cert, one side carrying GO's +1e9
                // FAKE flag). Its own category: nothing is missing, GO's duplicate-cert sweep has
                // already fired on one side, and there is no add that repairs it.
                bool reEncoded = sameCard && m.Encoded != e.Encoded;
                if (reEncoded)
                {
                    reEncodedTotal++;
                    reEncodedCerts.Add(theirCk);
                    if (reEncodings.Count < 8)
                        reEncodings.Add($"{GradedDesc(m)} here vs {GradedDesc(e)} on {who}");
                }

                if (!mineKeys.Contains(e.Key) && !reEncoded)
                {
                    peerOnlyTotal++;
                    if (peerExamples.Count < 8)
                        peerExamples.Add(GradedDesc(e));
                    // Only cards this install could actually PLACE, AND whose cert this PC does
                    // not already hold, become adopt candidates - so the button's count is the
                    // number it will really add. A card from a content pack this PC does not
                    // have still belongs in the REPORT (that difference is real and is worth
                    // naming) but adopting it is impossible - GetCardSaveIndex would mis-index
                    // it into save slot 0 or throw. A card whose cert is already held here is
                    // refused by GradedAdopt for the reason spelled out on that guard, so
                    // offering it would promise an add that never happens.
                    if (certHeldHere)
                        peerOnlyCertHeld++;
                    else if (CardSetInstalledHere(e.ToCard()))
                        peerOnly.Add(e);
                    else
                        peerOnlyNoContent++;
                }

                // CERT COLLISION - a separate category on purpose, and the one that silently
                // turns real cards into fakes. Certs are only unique while both GO save stores
                // agree; a role swap, or a session where the sidecar did not apply, leaves two
                // machines issuing the same serial. Merging those two cards onto one PC is what
                // GO's duplicate-cert sweep flags FAKE, so this is reported and NEVER repaired.
                if (certHeldHere && !sameCard)
                {
                    collisionTotal++;
                    int company, cert;
                    Util.GradingInterop.DecodeCert(e.Encoded, out company, out cert);
                    if (collisions.Count < 8)
                        collisions.Add($"cert {cert} is {CardIdentFull(m)} here and {CardIdentFull(e)} on {who}");
                }
            }

            int oursOnly = 0;
            var ourExamples = new List<string>();
            for (int i = 0; i < mine.Count; i++)
            {
                if (theirKeys.Contains(mine[i].Key))
                    continue;
                // the other half of a re-encoding pair - already reported as its own category
                if (reEncodedCerts.Contains(Util.GradingInterop.CertKey(mine[i].Encoded)))
                    continue;
                oursOnly++;
                if (ourExamples.Count < 8)
                    ourExamples.Add(GradedDesc(mine[i]));
            }

            if (peerOnly.Count > 0)
                _gradedPeerOnly[connId] = peerOnly;
            else
                _gradedPeerOnly.Remove(connId);
            RebuildGradedAdoptOffers();

            if (oursOnly == 0 && peerOnlyTotal == 0 && collisionTotal == 0 && reEncodedTotal == 0)
            {
                CoopPlugin.Log.LogInfo($"graded album check: identical ({mine.Count} graded cards)");

                // ANSWER EVERY DIGEST, NOT ONLY A DIVERGENT ONE. This reply is DATA and is never
                // gated by the alert config. Until 1.0.42 the host replied only from the
                // divergence tail below, which meant the guest re-ran this compare only while a
                // difference persisted: the moment the albums healed the host went quiet, the
                // guest's _gradedPeerOnly was never rewritten, the else-branch that erases it was
                // never reached, and its adopt button stood on a minutes-old list for the rest of
                // the session. A ten-second transient became a permanent button. With this reply
                // the guest sees the match, clears the list and retracts the row.
                // NO PING-PONG: the reply stays gated on isHost, and a guest that receives it
                // takes this same early return with isHost false, so the exchange is still
                // exactly one reply per guest-initiated digest.
                if (isHost)
                    SendGradedDigest(connId, mine);

                // Withdraw the cry. A digest pair can still straddle a real in-flight change
                // (a card between two mirrored containers, a shelf placement racing the 45s
                // tick), so a check that finds nothing must be able to take the alarm back.
                bool alertStood = _gradedAlertStanding.Remove(connId);
                // SCREEN, so it follows the alert config by construction: an all-clear can only
                // fire for an alarm this config actually let onto the screen.
                if (isHost && alertStood)
                {
                    const string clear = "graded albums match now - the earlier difference is gone";
                    RegisterLine = clear;
                    RegisterLineTimer = 8f;
                    Send(connId, new ToastMessage { Text = clear });
                }
                return;
            }

            string summary = $"heads-up: graded albums differ ({oursOnly} only here, {peerOnlyTotal} only on {who}) - nothing was changed"
                + (peerOnly.Count > 0 ? "; the co-op panel can adopt the " + peerOnly.Count + " you're missing" : "")
                + (peerOnlyNoContent > 0 ? $" ({peerOnlyNoContent} of them are from content packs this PC doesn't have)" : "")
                + (peerOnlyCertHeld > 0 ? $" ({peerOnlyCertHeld} can't be adopted - this PC already holds those certificate numbers)" : "")
                + (reEncodedTotal > 0 ? $"; {reEncodedTotal} more are the SAME card with a different grade encoding" : "");

            // SCREEN ONLY, AND DECIDED ABOVE THE LOG ON PURPOSE. Everything below this point that
            // writes RegisterLine or sends a Toast is gated by this flag; NOTHING else is. The
            // LogWarning immediately below, the RE-ENCODED line, the CERT COLLISION line and the
            // reply digest all run whatever the player chose - a log the player hands to someone
            // else has to say everything the check found, and the reply digest is the only way the
            // peer learns about its own half of the difference. The adopt button is not gated
            // either: "stop shouting at me" is not "hide the repair".
            //
            // AND IT IS THE HOST'S COPY OF THE SETTING THAT DECIDES, in both directions. Every
            // screen-facing statement in this method - the RegisterLine, the Toast, and the
            // all-clear above - sits inside `if (isHost)`, so on a guest this flag is computed
            // and never read: a joiner who picks Never still receives the host's heads-up, and a
            // joiner who picks Always still gets nothing if the host picked Never. That is
            // deliberate for now and the config description says so out loud. Closing the gap
            // would mean putting a preference byte on the wire and having the host keep per-conn
            // preference state across rejoins - new wire surface in the release whose whole point
            // is that this diagnostic over-reached - and it would let a guest silence the ONLY
            // graded-drift signal that ever reaches them, about their own album.
            var alertMode = CoopPlugin.GradedDriftAlert.Value;
            bool showAlert = alertMode == GradedAlertMode.Always
                || (alertMode == GradedAlertMode.OncePerSession && !_gradedAlertEverShown.Contains(connId));

            CoopPlugin.Log.LogWarning("graded album check: " + summary
                + (ourExamples.Count > 0 ? " | only here e.g.: " + string.Join(" / ", ourExamples.ToArray()) : "")
                + (peerExamples.Count > 0 ? $" | only on {who} e.g.: " + string.Join(" / ", peerExamples.ToArray()) : ""));
            if (reEncodedTotal > 0)
                CoopPlugin.Log.LogWarning($"graded album check: RE-ENCODED - {reEncodedTotal} cert(s) sit on the SAME card on both PCs but carry a DIFFERENT encoded grade. "
                    + "This is NOT a missing card and adopting it would not repair it: the two rows are one card in two states, and the usual cause is Grading Overhaul's "
                    + "duplicate-cert sweep having already fired on one side and rewritten that row to its FAKE encoding (+1,000,000,000 - e.g. 1380002639 against a clean 380002639). "
                    + "Adding the clean twin here would only make GO flag BOTH, so these are reported and never offered for adoption. | " + string.Join(" / ", reEncodings.ToArray()));
            if (collisionTotal > 0)
                CoopPlugin.Log.LogWarning($"graded album check: CERT COLLISION - {collisionTotal} cert(s) exist on BOTH PCs bound to DIFFERENT cards. "
                    + "This is NOT a missing card and there is NO automated repair: bringing both copies onto one PC is exactly what makes Grading Overhaul flag both of them FAKE. "
                    + "The two save stores have drifted apart and one side's certs need re-issuing by hand. | " + string.Join(" / ", collisions.ToArray()));

            if (isHost)
            {
                if (showAlert)
                {
                    _gradedAlertEverShown.Add(connId);
                    _gradedAlertStanding.Add(connId);
                    RegisterLine = summary;
                    RegisterLineTimer = 10f;
                    // Written from the GUEST's point of view, not reused from the host's summary:
                    // "only here" on the host means "only on yours" to the reader of this toast, and
                    // a heads-up that says the opposite of what the player sees is worse than none.
                    string toast = $"heads-up: your graded albums differ ({peerOnlyTotal} graded cards only on yours, {oursOnly} only on the host's) - nothing was changed"
                        + (oursOnly > 0 ? "; open the co-op panel to adopt the ones you're missing" : "")
                        + (collisionTotal > 0 ? " - and some certificate numbers clash, see the log" : "")
                        + (reEncodedTotal > 0 ? $" - and {reEncodedTotal} card(s) carry a different grade encoding on each PC, see the log" : "");
                    Send(connId, new ToastMessage { Text = toast });
                }
                // Send OUR digest back so the guest can see the half of the difference that is on
                // ITS side, and offer the same adopt button. Same writer, opposite direction.
                // ALWAYS - this is data, and gating it would blind the guest, not quiet it.
                SendGradedDigest(connId, mine);
            }
        }

        /// <summary>Counts only the ADOPTABLE entries, never the list length: GradedAdopt leaves
        /// the ones it refused in <see cref="_gradedPeerOnly"/> (marked) so the difference is
        /// still reportable, and a peer whose whole diff turned out to be unrepairable must show
        /// NO button at all rather than one that promises an add and then refuses every row.</summary>
        private void RebuildGradedAdoptOffers()
        {
            GradedAdoptOffers.Clear();
            foreach (var kv in _gradedPeerOnly)
            {
                if (kv.Value == null || kv.Value.Count == 0)
                    continue;
                int adoptable = 0;
                for (int i = 0; i < kv.Value.Count; i++)
                    if (!kv.Value[i].Refused)
                        adoptable++;
                if (adoptable == 0)
                    continue;
                string who = PeerNames.TryGetValue(kv.Key, out var nm) ? nm
                    : (Role == CoopRole.Host ? "the joiner" : "the host");
                GradedAdoptOffers.Add(new GradedAdoptOffer { ConnId = kv.Key, Who = who, Count = adoptable });
            }
        }

        /// <summary>The F2 button. ONE WAY, ADD ONLY, NEVER AUTOMATIC: it adds the graded cards
        /// the peer reported and we do not have, and it removes nothing, ever.
        ///
        /// THE LOAD-BEARING GUARD IS THE CERT ONE, AND IT REFUSES ON CERT PRESENCE ALONE.
        /// Grading Overhaul's duplicate-cert sweep (AntiCheat_AddCard_Patch, decompiled-grading
        /// :8534-8573) matches candidates on (company, cert) and NOTHING ELSE - it never compares
        /// card identity - and it reaches the comparison through Helper.DecodeGradeFull, which
        /// STRIPS the +1,000,000,000 FAKE flag before decoding (:15953). Two consequences, both
        /// of which the old "cert on a DIFFERENT card" test walked straight into:
        ///  - a FAKE-flagged local twin of the very same card decodes to the very same
        ///    (company, cert), so it is already in our cert map and GO already counts it;
        ///  - adopting past it calls AddCard, the sweep sees two rows on one cert, and it rewrites
        ///    BOTH to the FAKE encoding.
        /// The old guard waved that same-card twin through because the expansion/monster matched.
        /// GO then mutated the freshly adopted copy, mineKeys had recorded the CLEAN key, so the
        /// next digest still reported the card as missing and every press appended another FAKE
        /// row. Hence: if the cert exists here at all, in any card, in any encoding, refuse.
        ///
        /// The rest of the path is the one ApplyCardDelta already uses for a received graded card:
        /// Remember (burns + binds the cert so GO's anti-cheat leaves it alone), then AddCard.
        /// ApplyingRemoteCards is held over the loop so our own AddCard postfix does not forward
        /// the repair back to the peer as a fresh card.</summary>
        public void GradedAdopt(int connId)
        {
            if (!_gradedPeerOnly.TryGetValue(connId, out var wanted) || wanted == null || wanted.Count == 0)
                return;
            // Without GO there is no cert to burn or bind, so every added card would land on GO's
            // absent anti-cheat as an unvouched encoded grade the moment the peer installs it -
            // and the digest that produced this list is itself empty-by-construction here. Say so
            // rather than adding cards nothing on this PC can account for.
            if (!Util.GradingInterop.Present)
            {
                RegisterLine = "Grading Overhaul isn't loaded here - graded cards can't be adopted";
                RegisterLineTimer = 8f;
                CoopPlugin.Log.LogWarning("graded adopt: refused - Grading Overhaul is not present on this PC, so a received cert cannot be burned or bound");
                return;
            }
            if (!InGameLevel())
            {
                RegisterLine = "load into the shop first, then adopt";
                RegisterLineTimer = 6f;
                return;
            }

            // Rebuilt AT PRESS TIME, never reused from the digest-time snapshot. Minutes can pass
            // between the digest and the click, and this union is the only thing standing between
            // the peer's list and a duplicate AddCard - a stale one re-offers cards that have
            // since arrived by any other route (delta sync, a grading job maturing, a box opened).
            //
            // THE FULL UNION HERE, BOTH SETS FROM IT. Unlike CompareGradedDigests, NOTHING in
            // this method goes on the wire and nothing is compared against the peer's view, so
            // there is no reason to stay narrow and every reason not to: a card in the player's
            // hand or staged on the grading submit screen is a card this PC already HAS, so it
            // must count as `alreadyHere`, and its cert must count as a clash. Grading Overhaul
            // reads those same two containers in its AddCard duplicate-cert scan
            // (decompiled-grading :8554-8573) and would flag BOTH copies FAKE the moment this
            // button added a second row on that cert.
            var mine = Util.GradingInterop.BuildGradedCertInventory();
            // Null is "cannot check now" - see BuildGradedCertInventory. During a world load the
            // live containers are still spawning, so the guard would be blind in exactly the
            // direction that lets a duplicate through. Refuse rather than adopt against a partial
            // view; the offer is still there when the world has finished loading.
            if (mine == null)
            {
                RegisterLine = "still loading the shop - try adopting again in a moment";
                RegisterLineTimer = 6f;
                CoopPlugin.Log.LogWarning("graded adopt: refused - the world is still loading, so this PC cannot yet see every place a graded card lives; adopting now could duplicate a certificate");
                return;
            }
            mine.AddRange(Util.GradingInterop.LocalOnlyGradedCerts());
            var mineKeys = new HashSet<string>();
            var mineByCert = new Dictionary<long, Util.GradingInterop.GradedEntry>();
            for (int i = 0; i < mine.Count; i++)
            {
                mineKeys.Add(mine[i].Key);
                long ck0 = Util.GradingInterop.CertKey(mine[i].Encoded);
                if (ck0 != 0L && !mineByCert.ContainsKey(ck0))
                    mineByCert[ck0] = mine[i];
            }

            int added = 0, alreadyHere = 0, certClash = 0, noContent = 0;
            // Refused candidates are KEPT (marked) so the difference stays visible in the report
            // instead of vanishing with the button; RebuildGradedAdoptOffers counts only the
            // unmarked ones, so a peer whose whole diff is unrepairable shows no button at all.
            var keep = new List<Util.GradingInterop.GradedEntry>();
            Patches.GamePatches.ApplyingRemoteCards = true;
            try
            {
                for (int i = 0; i < wanted.Count; i++)
                {
                    var e = wanted[i];
                    e.Refused = false;
                    if (mineKeys.Contains(e.Key))
                    {
                        alreadyHere++;
                        continue;
                    } // arrived since the digest
                    var card = e.ToCard();
                    if (!CardSetInstalledHere(card))
                    {
                        noContent++;
                        e.Refused = true;
                        keep.Add(e);
                        CoopPlugin.Log.LogWarning($"graded adopt: {GradedDesc(e)} is from a card set you don't have installed - skipped");
                        continue;
                    }
                    long ck = Util.GradingInterop.CertKey(e.Encoded);
                    if (ck != 0L && mineByCert.TryGetValue(ck, out var clash))
                    {
                        certClash++;
                        e.Refused = true;
                        keep.Add(e);
                        // Two genuinely different faults, so two distinct wordings - reading
                        // "already exists on a different card" under a same-card FAKE twin is
                        // what sent the last investigation looking for a card that was never
                        // there. SameCard compares the full save-index identity, not just the
                        // monster, so a border/foil variant still reads as the collision it is.
                        // The ALARMING arm now prints the FULL save-index identity on both sides so
                        // the line can be CHECKED rather than appearing to name one card twice: the
                        // field report that read "cert 4050 is AscendedHeroesEXP#411 here and
                        // AscendedHeroesEXP#411 on rocio" was almost certainly a REAL collision and
                        // merely illegible, the two entries differing by border, foil, destiny or
                        // expansion - none of which CardIdent is guaranteed to print (see
                        // CardIdentFull). Both arms refuse the adopt either way - the choice only
                        // ever decides what the log says.
                        if (!SameCard(clash, e))
                            CoopPlugin.Log.LogWarning($"graded adopt: REFUSED {GradedDesc(e, full: true)} - that certificate number is already on this PC bound to a DIFFERENT card, {GradedDesc(clash, full: true)}. "
                                + "Adding it would make Grading Overhaul flag BOTH cards FAKE, so it is left alone.");
                        else
                            CoopPlugin.Log.LogWarning($"graded adopt: REFUSED {GradedDesc(e)} - you already hold that cert; the local copy is FAKE-flagged (or identical): {GradedDesc(clash)}. "
                                + "Grading Overhaul's duplicate-cert sweep matches on (company, cert) alone and decodes past the FAKE flag, so adopting would make it flag both.");
                        continue;
                    }
                    Util.GradingInterop.Remember(card);
                    CPlayerData.AddCard(card, 1);
                    added++;
                    mineKeys.Add(e.Key);
                    if (ck != 0L && !mineByCert.ContainsKey(ck))
                        mineByCert[ck] = e;
                }
            }
            catch (Exception ex) { CoopPlugin.Log.LogWarning("graded adopt: " + ex.Message); }
            finally { Patches.GamePatches.ApplyingRemoteCards = false; }

            _binderRefreshPending = true;
            if (keep.Count > 0)
                _gradedPeerOnly[connId] = keep;
            else
                _gradedPeerOnly.Remove(connId);
            RebuildGradedAdoptOffers();
            _lastGradedHash = -1; // our album changed - re-digest on the next client tick

            string line = $"adopted {added} graded card(s)"
                + (alreadyHere > 0 ? $", {alreadyHere} already here" : "")
                + (certClash > 0 ? $", {certClash} refused (cert already on this PC - see the log)" : "")
                + (noContent > 0 ? $", {noContent} from missing content packs" : "");
            // A PRESS CHANGES NO ALARM STATE, AND THAT IS THE CONTRACT - do not "clear the
            // warning" here. The only thing that retracts a graded-drift alert is a later digest
            // that comes back IDENTICAL: CompareGradedDigests clears _gradedAlertStanding and
            // sends the all-clear, and only the HOST ever does either (the guest's standing set
            // is always empty, so nothing on the guest is waiting to be retracted). An adopt does
            // not prove the albums match - it repairs at most the half that is add-only, and what
            // typically REMAINS is one-sided the other way (cards only here) or unrepairable
            // (refused certs), which is precisely the case where the alarm SHOULD still stand.
            // Zero adoptable candidates remain for this peer by construction, every entry that
            // survived into `keep` was marked Refused, and the summary below is the whole of this
            // button's feedback.
            RegisterLine = line;
            RegisterLineTimer = 8f;
            CoopPlugin.Log.LogInfo("graded adopt: " + line);
        }

    }
}
