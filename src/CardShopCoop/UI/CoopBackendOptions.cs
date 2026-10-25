using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CardShopCoop.Net.Networking;
using TMPro;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>Renders connection forms from the backend contract. Nothing here knows which
    /// backend is IP, Steam, or a third-party transport: fields, actions and live information all
    /// come from <see cref="INetworkingBackend"/>.</summary>
    internal static class CoopBackendOptions
    {
        private const float LiveHeaderWidth = 460f;
        private const float SectionPadding = 20f;
        private const float SectionGap = 12f;
        private const float SectionTitleHeight = 36f;
        private const float FieldLabelWidth = 220f;
        private const float FieldLabelGap = 12f;
        private const float PasteWidth = 64f;
        private const float AfterInputWidth = 6f + PasteWidth + 6f;
        private const float ActionWidth = 130f;
        private const float ActionStep = 140f;
        private const float FieldRowStep = 52f;
        private const float ToggleRowStep = 48f;
        private const float StartRowStep = 54f;
        private const float ActionRowStep = 44f;
        private static readonly Dictionary<string, FieldValues> Values = new();

        internal static void ResetValues() => Values.Clear();

        internal static string Signature(ConnectMode mode)
        {
            var core = CoopCore.Instance;
            var text = new StringBuilder(mode.ToString());
            text.Append('|').Append((int)CoopCore.Role);
            if (core == null)
            {
                return text.ToString();
            }

            foreach (var backend in core.Backends)
            {
                var info = backend.ConnectionInfo;
                text.Append('|').Append(backend.Id).Append(':').Append(backend.DisplayName)
                    .Append(':').Append(info.Active).Append(':').Append(info.IsHost)
                    .Append(':').Append(info.Busy);
                var fields = backend.GetFields(mode);
                var values = ValuesFor(backend, mode, fields);
                foreach (var field in fields)
                {
                    text.Append('|').Append((int)field.Id).Append(':').Append(field.Kind)
                        .Append(':').Append(field.Label).Append(':').Append(field.Required);
                    // A toggle draws its own value ("[x] On"), so flipping one changes what the
                    // window must show and has to invalidate the form. TEXT values are
                    // deliberately excluded: they draw from the live TMP_InputField, and including
                    // them would rebuild (and destroy the focused field) while the player types.
                    if (field.Kind == FieldKind.Toggle)
                    {
                        text.Append(':').Append(values.GetBool(field.Id) ? '1' : '0');
                    }
                }

                foreach (var action in backend.Actions)
                {
                    if (action.Mode != null && action.Mode != mode)
                    {
                        continue;
                    }

                    text.Append('|').Append(action.Id).Append(':').Append(action.Label)
                        .Append(':').Append((int?)action.Mode).Append(':').Append(action.Phase)
                        .Append(':').Append((int?)action.Field).Append(':').Append(action.Enabled);
                }
            }

            return text.ToString();
        }

        internal static float Build(Transform parent, ConnectMode mode, float width,
            Action<string> hint, Action rebuild)
        {
            var core = CoopCore.Instance;
            if (core == null)
            {
                return 0f;
            }

            var backends = core.Backends;
            if (backends.Count == 0)
            {
                NativeCoopUi.Text(parent, "No transports are available.", 18f, 0f, 0f,
                    width, 30f, NativeCoopUi.Dim, TextAlignmentOptions.Left,
                    wrap: false, title: false);
                return 30f;
            }

            // One full-width section per backend, top to bottom. Each section reports the height
            // its own fields actually need, so a backend with a conditional field (Steam's lobby
            // name) grows instead of leaving the layout at a fixed column height.
            var y = 0f;
            for (var i = 0; i < backends.Count; i++)
            {
                y += BuildBackend(parent, backends[i], mode, width, y, hint, rebuild);
                if (i < backends.Count - 1)
                {
                    y += SectionGap;
                }
            }

            return y;
        }

        internal static float BuildLive(Transform parent, Action<string> hint)
        {
            var core = CoopCore.Instance;
            if (core == null)
            {
                return 0f;
            }

            INetworkingBackend active = null;
            ConnectionInfo info = default;
            foreach (var backend in core.Backends)
            {
                var candidate = backend.ConnectionInfo;
                if (!candidate.Active && !candidate.Busy)
                {
                    continue;
                }

                active = backend;
                info = candidate;
                break;
            }

            if (active == null)
            {
                NativeCoopUi.Text(parent, "Not connected.", 18f, 0f, 0f, 600f, 30f,
                    NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
                return 30f;
            }

            var mode = info.IsHost ? ConnectMode.Host : ConnectMode.Join;
            NativeCoopUi.Text(parent, active.DisplayName, 24f, 0f, 0f, LiveHeaderWidth, 32f,
                NativeCoopUi.Gold, TextAlignmentOptions.Left, wrap: false, title: true);
            NativeCoopUi.Text(parent, info.Summary ?? "", 18f, 0f, 36f, 900f, 26f,
                Color.white, TextAlignmentOptions.Left, wrap: false, title: false);
            var y = 66f;
            if (!string.IsNullOrEmpty(info.Warning))
            {
                NativeCoopUi.Text(parent, info.Warning, 16f, 0f, y, 900f, 40f,
                    NativeCoopUi.Bad, TextAlignmentOptions.Left, wrap: true, title: false);
                y = 108f;
            }

            var actionX = 0f;
            foreach (var action in LiveActions(active, mode))
            {
                var button = NativeCoopUi.Button(parent, action.Label,
                    action.Enabled ? NativeCoopUi.Blue : NativeCoopUi.Grey,
                    () => action.Invoke(), actionX, y, 200f, 40f, 16f);
                button.SetInteractable(action.Enabled);
                actionX += 210f;
            }

            UnityEngine.Events.UnityAction stop = info.IsHost
                ? (UnityEngine.Events.UnityAction)(() => core.StopHosting(active))
                : () => core.Disconnect();
            NativeCoopUi.Button(parent, info.IsHost ? "Stop hosting" : "Leave shop",
                NativeCoopUi.Orange, stop, actionX, y, 200f, 40f, 16f);
            return y + 50f;
        }

        private static float BuildBackend(Transform parent, INetworkingBackend backend,
            ConnectMode mode, float width, float top, Action<string> hint, Action rebuild)
        {
            var fields = backend.GetFields(mode);
            var values = ValuesFor(backend, mode, fields);
            var info = backend.ConnectionInfo;
            var panelActions = PanelActions(backend, mode).ToList();

            // The section's height is its own content's height, computed the same way the rows
            // below advance. Nothing is padded to a fixed column size, so adding a backend stacks
            // under the previous one and adding a field grows that section only.
            var height = SectionPadding * 2f + SectionTitleHeight;
            for (var i = 0; i < fields.Count; i++)
            {
                height += fields[i].Kind == FieldKind.Toggle ? ToggleRowStep : FieldRowStep;
            }
            height += StartRowStep + panelActions.Count * ActionRowStep;

            // An outlined panel rather than a filled block: the buttons and inputs inside keep
            // their own contrast, and consecutive sections stay visually separate in the scroll.
            NativeCoopUi.Flat(parent, 0f, top, width, height, NativeCoopUi.PanelEdge);
            NativeCoopUi.Flat(parent, 2f, top + 2f, width - 4f, height - 4f,
                NativeCoopUi.PanelFill);

            var x = SectionPadding;
            var innerWidth = width - SectionPadding * 2f;
            var y = top + SectionPadding;
            NativeCoopUi.Text(parent, backend.DisplayName, 24f, x, y, innerWidth, 32f,
                NativeCoopUi.Gold, TextAlignmentOptions.Left, wrap: false, title: true);
            y += SectionTitleHeight;

            foreach (var field in fields)
            {
                if (field.Kind == FieldKind.Toggle)
                {
                    NativeCoopUi.Checkbox(parent, values.GetBool(field.Id), field.Label, x, y,
                        innerWidth, ToggleRowStep, 18f, () =>
                        {
                            values.SetBool(field.Id, !values.GetBool(field.Id));
                            backend.Configure(values);
                            rebuild();
                        });
                    y += ToggleRowStep;
                    continue;
                }

                var fieldActions = FieldActions(backend, mode, field.Id).ToList();
                NativeCoopUi.Text(parent, field.Label, 18f, x, y, FieldLabelWidth, 40f,
                    Color.white, TextAlignmentOptions.MidlineLeft, wrap: false, title: false);

                var inputX = x + FieldLabelWidth + FieldLabelGap;
                var reserved = AfterInputWidth + fieldActions.Count * ActionStep;
                var inputWidth = Mathf.Max(160f, innerWidth - FieldLabelWidth - FieldLabelGap
                    - reserved);
                var input = NativeCoopUi.Input(parent, field.Label, inputX, y, inputWidth, 40f,
                    18f, Limit(field), ContentType(field));
                input.text = values[field.Id] ?? "";
                var fieldId = field.Id;
                input.onValueChanged.AddListener(text =>
                {
                    values[fieldId] = text;
                    backend.Configure(values);
                });

                var actionX = inputX + inputWidth + 6f;
                NativeCoopUi.Button(parent, "Paste", NativeCoopUi.Grey,
                    () => Paste(input, field, values, backend, hint), actionX, y, PasteWidth, 40f,
                    15f);
                actionX += 70f;
                for (var i = 0; i < fieldActions.Count; i++)
                {
                    var action = fieldActions[i];
                    var button = NativeCoopUi.Button(parent, action.Label,
                        action.Enabled ? NativeCoopUi.Blue : NativeCoopUi.Grey,
                        () => action.Invoke(), actionX, y, ActionWidth, 40f, 15f);
                    button.SetInteractable(action.Enabled);
                    actionX += ActionStep;
                }

                y += FieldRowStep;
            }

            var startLabel = (mode == ConnectMode.Host ? "Host via " : "Join via ")
                + backend.DisplayName;
            var start = NativeCoopUi.Button(parent, startLabel, NativeCoopUi.Green,
                () => Start(backend, mode, values, hint), x, y, 320f, 46f, 18f);
            start.SetInteractable(!info.Busy);
            y += StartRowStep;

            foreach (var action in panelActions)
            {
                var button = NativeCoopUi.Button(parent, action.Label,
                    action.Enabled ? NativeCoopUi.Blue : NativeCoopUi.Grey,
                    () => action.Invoke(), x, y, 320f, 40f, 16f);
                button.SetInteractable(action.Enabled);
                y += ActionRowStep;
            }

            return height;
        }

        private static void Start(INetworkingBackend backend, ConnectMode mode, FieldValues values,
            Action<string> hint)
        {
            var core = CoopCore.Instance;
            if (core == null)
            {
                return;
            }

            backend.Configure(values);
            core.ConnectBackend(backend, mode, values);
            hint((mode == ConnectMode.Host ? "Hosting via " : "Joining via ")
                + backend.DisplayName + "...");
        }

        private static FieldValues ValuesFor(INetworkingBackend backend, ConnectMode mode,
            IReadOnlyList<BackendField> fields)
        {
            var key = backend.Id + "|" + mode;
            if (!Values.TryGetValue(key, out var values))
            {
                values = new FieldValues();
                Values[key] = values;
            }

            for (var i = 0; i < fields.Count; i++)
            {
                if (!values.Contains(fields[i].Id))
                {
                    values[fields[i].Id] = fields[i].Value;
                }
            }

            return values;
        }

        private static IEnumerable<BackendAction> FieldActions(INetworkingBackend backend,
            ConnectMode mode, FieldId field)
            => backend.Actions.Where(action => action.Field == field
                && (action.Mode == null || action.Mode == mode)
                && action.Phase != ActionPhase.Live);

        private static IEnumerable<BackendAction> PanelActions(INetworkingBackend backend,
            ConnectMode mode)
            => backend.Actions.Where(action => action.Field == null
                && (action.Mode == null || action.Mode == mode)
                && action.Phase != ActionPhase.Live);

        private static IEnumerable<BackendAction> LiveActions(INetworkingBackend backend,
            ConnectMode mode)
            => backend.Actions.Where(action => action.Field == null
                && (action.Mode == null || action.Mode == mode)
                && action.Phase != ActionPhase.BeforeStart);

        private static void Paste(TMP_InputField input, BackendField field, FieldValues values,
            INetworkingBackend backend, Action<string> hint)
        {
            string clipboard;
            try
            {
                clipboard = GUIUtility.systemCopyBuffer;
            }
            catch (Exception error)
            {
                hint("Could not read the clipboard: " + error.Message);
                return;
            }

            if (string.IsNullOrEmpty(clipboard))
            {
                hint("The clipboard is empty.");
                return;
            }

            if (field.Kind != FieldKind.Password)
            {
                clipboard = clipboard.Trim();
            }
            input.text = clipboard;
            values[field.Id] = input.text;
            backend.Configure(values);
            input.caretPosition = input.text.Length;
            hint("Pasted into " + field.Label + ".");
        }

        private static int Limit(BackendField field)
            => field.Kind == FieldKind.Port || field.Kind == FieldKind.Number
                ? 6
                : field.Kind == FieldKind.Password ? 128 : 128;

        private static TMP_InputField.ContentType ContentType(BackendField field)
            => field.Kind == FieldKind.Password
                ? TMP_InputField.ContentType.Password
                : field.Kind == FieldKind.Port || field.Kind == FieldKind.Number
                    ? TMP_InputField.ContentType.IntegerNumber
                    : TMP_InputField.ContentType.Standard;
    }
}
