using global::Android.App;
using global::Android.Widget;
using Ryujinx.HLE;
using Ryujinx.HLE.HOS.Applets;
using Ryujinx.HLE.HOS.Services.Am.AppletOE.ApplicationProxyService.ApplicationProxy.Types;
using Ryujinx.HLE.UI;
using RyujinxSwitch = Ryujinx.HLE.Switch;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Android
{
    internal sealed class AndroidHostUIHandler : IHostUIHandler
    {
        private readonly Activity _activity;
        public IHostUITheme HostUITheme { get; } = new AndroidHostUITheme();

        public AndroidHostUIHandler(Activity activity) => _activity = activity;

        public bool DisplayInputDialog(SoftwareKeyboardUIArgs args, out string userText)
        {
            userText = args.InitialText ?? string.Empty;
            if (global::Android.OS.Looper.MyLooper() == global::Android.OS.Looper.MainLooper)
                return false;

            string result = null;
            bool accepted = false;
            using var completed = new ManualResetEventSlim(false);

            _activity.RunOnUiThread(() =>
            {
                var input = new EditText(_activity) { Text = args.InitialText ?? string.Empty };
                if (args.StringLengthMax > 0)
                {
                    input.SetFilters(new global::Android.Text.IInputFilter[]
                    {
                        new global::Android.Text.InputFilterLengthFilter(args.StringLengthMax)
                    });
                }

                var builder = new AlertDialog.Builder(_activity);
                if (!string.IsNullOrWhiteSpace(args.HeaderText)) builder.SetTitle(args.HeaderText);
                if (!string.IsNullOrWhiteSpace(args.SubtitleText)) builder.SetMessage(args.SubtitleText);
                builder.SetView(input);
                builder.SetPositiveButton(string.IsNullOrWhiteSpace(args.SubmitText) ? "OK" : args.SubmitText,
                    (sender, eventArgs) => { result = input.Text ?? string.Empty; accepted = true; completed.Set(); });
                builder.SetNegativeButton("Cancelar", (sender, eventArgs) => completed.Set());
                var dialog = builder.Create();
                dialog.CancelEvent += (sender, eventArgs) => completed.Set();
                dialog.SetCancelable(true);
                dialog.Show();
            });

            if (!completed.Wait(TimeSpan.FromMinutes(5))) return false;
            userText = result ?? string.Empty;
            return accepted && userText.Length >= args.StringLengthMin;
        }

        public bool DisplayMessageDialog(string title, string message)
        {
            if (_activity.IsFinishing) return false;
            _activity.RunOnUiThread(() => Toast.MakeText(_activity, title + ": " + message, ToastLength.Long).Show());
            return true;
        }

        public bool DisplayMessageDialog(ControllerAppletUIArgs args)
            => DisplayMessageDialog("Controles", "Use os botões e analógicos na tela ou um controle Bluetooth/USB.");

        public void ExecuteProgram(RyujinxSwitch device, ProgramSpecifyKind kind, ulong value)
        {
            device.Configuration.UserChannelPersistence.ExecuteProgram(kind, value);
            DisplayMessageDialog("Ryujinx", "O jogo solicitou reiniciar ou trocar de programa. Feche e abra o jogo novamente para aplicar.");
        }

        public bool DisplayErrorAppletDialog(string title, string message, string[] buttonsText)
        {
            if (global::Android.OS.Looper.MyLooper() == global::Android.OS.Looper.MainLooper)
            {
                DisplayMessageDialog(title, message);
                return false;
            }

            bool showDetails = false;
            using var completed = new ManualResetEventSlim(false);
            _activity.RunOnUiThread(() =>
            {
                var builder = new AlertDialog.Builder(_activity).SetTitle(title).SetMessage(message);
                string[] buttons = buttonsText == null || buttonsText.Length == 0 ? new[] { "OK" } : buttonsText;
                for (int i = 0; i < buttons.Length; i++)
                {
                    int index = i;
                    if (i == 0)
                        builder.SetPositiveButton(buttons[i], (s, e) => { showDetails = buttons.Length > 1; completed.Set(); });
                    else if (i == 1)
                        builder.SetNegativeButton(buttons[i], (s, e) => completed.Set());
                    else
                        builder.SetNeutralButton(buttons[i], (s, e) => { showDetails = index != buttons.Length - 1; completed.Set(); });
                }
                var dialog = builder.Create();
                dialog.CancelEvent += (s, e) => completed.Set();
                dialog.Show();
            });
            completed.Wait(TimeSpan.FromMinutes(2));
            return showDetails;
        }

        public IDynamicTextInputHandler CreateDynamicTextInputHandler() => new AndroidDynamicTextInputHandler(_activity);
    }

    internal sealed class AndroidHostUITheme : IHostUITheme
    {
        public string FontFamily => "sans-serif";
        public ThemeColor DefaultBackgroundColor => new(1, 0, 0, 0);
        public ThemeColor DefaultForegroundColor => new(1, 1, 1, 1);
        public ThemeColor DefaultBorderColor => new(1, 1, 1, 1);
        public ThemeColor SelectionBackgroundColor => new(1, 1, 1, 1);
        public ThemeColor SelectionForegroundColor => new(1, 0, 0, 0);
    }

    internal sealed class AndroidDynamicTextInputHandler : IDynamicTextInputHandler
    {
        private readonly Activity _activity;
        private EditText _input;
        private AlertDialog _dialog;
        private volatile bool _enabled;
        private bool _updating;

        public event DynamicTextChangedHandler TextChangedEvent;
        public event KeyPressedHandler KeyPressedEvent;
        public event KeyReleasedHandler KeyReleasedEvent;

        public AndroidDynamicTextInputHandler(Activity activity) => _activity = activity;

        public bool TextProcessingEnabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                _activity.RunOnUiThread(() =>
                {
                    if (_activity.IsFinishing) return;
                    if (value) ShowEditor();
                    else _dialog?.Dismiss();
                });
            }
        }

        private void ShowEditor()
        {
            if (_dialog?.IsShowing == true) return;
            _input = new EditText(_activity) { Hint = "Digite aqui" };
            _input.SetSingleLine(true);
            _input.TextChanged += (sender, args) =>
            {
                if (_updating || !_enabled) return;
                TextChangedEvent?.Invoke(_input.Text ?? string.Empty, _input.SelectionStart, _input.SelectionEnd, false);
            };
            _dialog = new AlertDialog.Builder(_activity)
                .SetTitle("Teclado do jogo")
                .SetView(_input)
                .SetPositiveButton("Concluir", (s, e) => { _enabled = false; })
                .SetNegativeButton("Fechar", (s, e) => { _enabled = false; })
                .Create();
            _dialog.CancelEvent += (s, e) => _enabled = false;
            _dialog.Show();
            _input.RequestFocus();
        }

        public void SetText(string text, int cursorBegin)
        {
            SetText(text, cursorBegin, cursorBegin);
        }

        public void SetText(string text, int cursorBegin, int cursorEnd)
        {
            _activity.RunOnUiThread(() =>
            {
                if (_input == null) return;
                _updating = true;
                _input.SetText(text ?? string.Empty, global::Android.Widget.TextView.BufferType.Editable);
                int length = _input.Text?.Length ?? 0;
                _input.SetSelection(Math.Clamp(cursorBegin, 0, length), Math.Clamp(cursorEnd, 0, length));
                _updating = false;
            });
        }

        public void Dispose()
        {
            _enabled = false;
            _activity.RunOnUiThread(() =>
            {
                _dialog?.Dismiss();
                _dialog = null;
                _input = null;
            });
        }
    }
}
