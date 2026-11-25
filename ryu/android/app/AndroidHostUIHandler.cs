using global::Android.App;
using global::Android.Widget;
using Ryujinx.HLE;
using Ryujinx.HLE.HOS.Applets;
using Ryujinx.HLE.HOS.Services.Am.AppletOE.ApplicationProxyService.ApplicationProxy.Types;
using Ryujinx.HLE.UI;
using RyujinxSwitch = Ryujinx.HLE.Switch;

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
            var completed = new System.Threading.ManualResetEventSlim(false);

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
                if (!string.IsNullOrWhiteSpace(args.HeaderText))
                    builder.SetTitle(args.HeaderText);
                if (!string.IsNullOrWhiteSpace(args.SubtitleText))
                    builder.SetMessage(args.SubtitleText);
                builder.SetView(input);
                builder.SetPositiveButton(
                    string.IsNullOrWhiteSpace(args.SubmitText) ? "OK" : args.SubmitText,
                    (sender, eventArgs) =>
                    {
                        result = input.Text ?? string.Empty;
                        accepted = true;
                        completed.Set();
                    });
                builder.SetNegativeButton("Cancelar", (sender, eventArgs) => completed.Set());

                var dialog = builder.Create();
                dialog.SetCancelable(false);
                dialog.Show();
            });

            if (!completed.Wait(System.TimeSpan.FromMinutes(5)))
                return false;

            userText = result ?? string.Empty;
            return accepted && userText.Length >= args.StringLengthMin;
        }

        public bool DisplayMessageDialog(string title, string message)
        {
            _activity.RunOnUiThread(() => Toast.MakeText(_activity, title + ": " + message, ToastLength.Long).Show());
            return true;
        }

        public bool DisplayMessageDialog(ControllerAppletUIArgs args) => true;

        public void ExecuteProgram(RyujinxSwitch device, ProgramSpecifyKind kind, ulong value) { }

        public bool DisplayErrorAppletDialog(string title, string message, string[] buttonsText)
            => DisplayMessageDialog(title, message);

        public IDynamicTextInputHandler CreateDynamicTextInputHandler() => new AndroidDynamicTextInputHandler();
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
        public event DynamicTextChangedHandler TextChangedEvent;
        public event KeyPressedHandler KeyPressedEvent;
        public event KeyReleasedHandler KeyReleasedEvent;
        public bool TextProcessingEnabled { get; set; }
        public void SetText(string text, int cursorBegin) { }
        public void SetText(string text, int cursorBegin, int cursorEnd) { }
        public void Dispose() { }
    }
}
