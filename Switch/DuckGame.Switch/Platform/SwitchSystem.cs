using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DuckGame
{
    // Console features of the original Switch release (its NSwitch and SwitchSixAxis classes), implemented in
    // Switch/native/source/system_ui.c. Every call is a no-op off the console (the desktop benchmark build).
    public static class SwitchSystem
    {
        [DllImport("__Internal")]
        private static extern float switch_sixaxis_angle(int index);

        [DllImport("__Internal")]
        private static extern int switch_keyboard([MarshalAs(UnmanagedType.LPUTF8Str)] string header,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string initial, int maxLength, int filename, byte[] output, int outputSize);

        [DllImport("__Internal")]
        private static extern void switch_controller_config(int maxPlayers);

        [DllImport("__Internal")]
        private static extern void switch_lr_assignment(int on);

        [DllImport("__Internal")]
        private static extern int switch_account_name(byte[] output, int outputSize);

        private static bool _missing;

        private static bool Run(Action call)
        {
            if (_missing)
                return false;
            try
            {
                call();
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                _missing = true;
                return false;
            }
        }

        private static string Utf8(byte[] buffer)
        {
            int length = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
        }

        // Tilt of the controller in turns (one full turn = 1), as the original's SwitchSixAxis.GetAxis.
        public static float SixAxisAngle(int index)
        {
            float angle = 0f;
            Run(() => angle = switch_sixaxis_angle(index));
            return angle;
        }

        // Shows the system keyboard and waits for it. False when it can't be shown; text is null when cancelled.
        public static bool Keyboard(string header, string initial, int maxLength, bool filename, out string text)
        {
            // The system keyboard takes at most 500 characters.
            int length = Math.Clamp(maxLength, 1, 500);
            byte[] output = new byte[length * 4 + 1];
            int result = -1;
            text = null;
            if (!Run(() => result = switch_keyboard(header ?? "", initial ?? "", length, filename ? 1 : 0, output, output.Length)) || result < 0)
                return false;
            if (result > 0)
                text = Utf8(output);
            return true;
        }

        public static void ShowControllerConfig() => Run(() => switch_controller_config(Math.Min(8, MonoMain.MaximumGamepadCount)));

        public static void SetLRAssignment(bool on) => Run(() => switch_lr_assignment(on ? 1 : 0));

        private static bool _accountRead;
        private static string _accountName;

        // Nickname of the console user, read once; null if there is none.
        public static string AccountName
        {
            get
            {
                if (!_accountRead)
                {
                    _accountRead = true;
                    byte[] output = new byte[64];
                    int ok = 0;
                    if (Run(() => ok = switch_account_name(output, output.Length)) && ok != 0)
                        _accountName = Utf8(output);
                }
                return _accountName;
            }
        }
    }
}
