using OpenTap;

namespace InstrumentComponents.OpenTap.Tests;

internal static class InstrumentSettingsScope
{
    private static readonly object Gate = new();

    public static void Run(Action action)
    {
        lock (Gate)
        {
            var previous = InstrumentSettings.Current.ToArray();
            InstrumentSettings.Current.Clear();
            try
            {
                action();
            }
            finally
            {
                InstrumentSettings.Current.Clear();
                foreach (var instrument in previous)
                    InstrumentSettings.Current.Add(instrument);
            }
        }
    }
}
