using System;
using System.Collections.Generic;
using System.Linq;

namespace DuckGame
{
    // Desktop-only options (window, resolution, mouse, Windows audio engines, IME, TTS, browser,
    // dev console) are pruned from the menus DGR builds, so the shared Options.cs stays as is.
    public static class SwitchMenus
    {
        private static readonly string[] GraphicsItems =
        {
            "Fullscreen", "Resolution", "Windowed Fullscreen", "Console Width", "Console Height", "Console Scale",
        };

        private static readonly string[] AudioItems =
        {
            "Exclusive Mode can reduce", "audio latency, but will", "stop all other programs from",
            "making sound while Duck Game", "is running!", "Exclusive Mode", "Audio Engine", "Mute If In The Background",
        };

        private static readonly string[] UsabilityItems =
        {
            "IME Support", "Text To Speech", "|DGRED|Text To Speech Not Installed...",
        };

        private static readonly string[] RebuiltItems =
        {
            "Mouse Priority", "Show Workshop Mods", "Open URLs in Browser", "DSH in console", "Menu Mouse", "Invite Link",
        };

        public static void Prune()
        {
            // On by default while the port is being tuned; the toggle is under Graphics.
            DevConsole.showFPS = true;
            Remove(Options._lastCreatedGraphicsMenu, GraphicsItems);
            AddShowFps(Options._lastCreatedGraphicsMenu);
            Remove(Options._lastCreatedAudioMenu, AudioItems);
            Remove(Options._lastCreatedAccessibilityMenu, UsabilityItems);
            foreach (UIMenu menu in new[]
            {
                Options._lastCreatedDGREditorMenu, Options._lastCreatedDGRMiscMenu,
                Options._lastCreatedDGRHUDMenu, Options._lastCreatedDGRQOLMenu,
            })
                Remove(menu, RebuiltItems);
        }

        private static void AddShowFps(UIMenu menu)
        {
            if (menu == null)
                return;
            menu.Insert(new UIMenuItemToggle("Show FPS", field: new FieldBinding(typeof(DevConsole), nameof(DevConsole.showFPS))), 0);
        }

        private static void Remove(UIMenu menu, string[] labels)
        {
            if (menu == null)
                return;
            foreach (UIComponent item in menu.section.components.ToList())
                if (labels.Contains(Label(item)))
                    menu.Remove(item);

            // Removing items leaves blank spacer lines behind: drop leading and repeated ones.
            UIComponent previous = null;
            foreach (UIComponent item in menu.section.components.ToList())
            {
                if (IsSpacer(item) && (previous == null || IsSpacer(previous)))
                    menu.Remove(item);
                else
                    previous = item;
            }
        }

        private static bool IsSpacer(UIComponent component) => component is UIText text && text.text.Trim().Length == 0;

        private static string Label(UIComponent component)
        {
            if (component is UIText text)
                return text.text;
            foreach (UIComponent child in component.components)
            {
                string label = Label(child);
                if (!string.IsNullOrEmpty(label))
                    return label;
            }
            return null;
        }
    }
}
