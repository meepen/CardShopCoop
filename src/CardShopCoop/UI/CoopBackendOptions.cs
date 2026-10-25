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
        private const float ColumnWidth = 460f;
        private const float ColumnGap = 40f;
        private static readonly Dictionary<string, FieldValues> Values = new();

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
                foreach (var field in backend.GetFields(mode))
                {
                    text.Append('|').Append((int)field.Id).Append(':').Append(field.Kind)
                        .Append(':').Append(field.Label).Append(':').Append(field.Required);
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

        internal static float Build(Transform parent, ConnectMode mode, Action<string> hint,
            Action rebuild)
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
                    ColumnWidth, 30f, NativeCoopUi.Dim, TextAlignmentOptions.Left,
                    wrap: false, title: false);
                return 30f;
            }

            var height = 0f;
            for (var i = 0; i < backends.Count; i++)
            {
                var x = (i % 2) * (ColumnWidth + ColumnGap);
                var y = (i / 2) * 380f;
                var built = BuildBackend(parent, backends[i], mode, x, y, hint, rebuild);
                height = Mathf.Max(height, y + built);
            }

            return height;
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
            NativeCoopUi.Text(parent, active.DisplayName, 24f, 0f, 0f, ColumnWidth, 32f,
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
            ConnectMode mode, float x, float y, Action<string> hint, Action rebuild)
        {
            var fields = backend.GetFields(mode);
            var values = ValuesFor(backend, mode, fields);
            var info = backend.ConnectionInfo;
            NativeCoopUi.Text(parent, backend.DisplayName, 24f, x, y, ColumnWidth, 32f,
                NativeCoopUi.Gold, TextAlignmentOptions.Left, wrap: false, title: true);
            y += 36f;

            foreach (var field in fields)
            {
                if (field.Kind == FieldKind.Toggle)
                {
                    var isOn = values.GetBool(field.Id);
                    var button = NativeCoopUi.Button(parent,
                        (isOn ? "[x] " : "[ ] ") + field.Label, NativeCoopUi.Blue,
                        () =>
                        {
                            values.SetBool(field.Id, !values.GetBool(field.Id));
                            backend.Configure(values);
                            rebuild();
                        }, x, y, ColumnWidth, 38f, 18f);
                    y += 44f;
                    continue;
                }

                NativeCoopUi.Text(parent, field.Label, 16f, x, y, ColumnWidth, 22f,
                    Color.white, TextAlignmentOptions.Left, wrap: false, title: false);
                y += 22f;

                var fieldActions = FieldActions(backend, mode, field.Id).ToList();
                var inputWidth = fieldActions.Count > 0 ? 310f : ColumnWidth;
                var input = NativeCoopUi.Input(parent, field.Label, x, y, inputWidth, 40f,
                    18f, Limit(field), ContentType(field));
                input.text = values[field.Id] ?? "";
                var fieldId = field.Id;
                input.onValueChanged.AddListener(text =>
                {
                    values[fieldId] = text;
                    backend.Configure(values);
                });

                var actionX = x + inputWidth + 10f;
                for (var i = 0; i < fieldActions.Count; i++)
                {
                    var action = fieldActions[i];
                    var button = NativeCoopUi.Button(parent, action.Label,
                        action.Enabled ? NativeCoopUi.Blue : NativeCoopUi.Grey,
                        () => action.Invoke(), actionX, y, 130f, 40f, 15f);
                    button.SetInteractable(action.Enabled);
                    actionX += 140f;
                }

                y += 48f;
            }

            var startLabel = (mode == ConnectMode.Host ? "Host via " : "Join via ")
                + backend.DisplayName;
            var start = NativeCoopUi.Button(parent, startLabel, NativeCoopUi.Green,
                () => Start(backend, mode, values, hint), x, y, 320f, 46f, 18f);
            start.SetInteractable(!info.Busy);
            y += 54f;

            foreach (var action in PanelActions(backend, mode))
            {
                var button = NativeCoopUi.Button(parent, action.Label,
                    action.Enabled ? NativeCoopUi.Blue : NativeCoopUi.Grey,
                    () => action.Invoke(), x, y, 320f, 40f, 16f);
                button.SetInteractable(action.Enabled);
                y += 44f;
            }

            return y;
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
