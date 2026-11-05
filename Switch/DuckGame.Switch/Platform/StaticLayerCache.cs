using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;

namespace DuckGame
{
    // Draws the static tiles of the BLOCKS and BACKGROUND layers (tilesets, background tiles) once into
    // a world-space render target and blits that each frame instead of pushing ~1500 sprites through
    // the sprite pipeline. Moving or otherwise dynamic things of the layer are still drawn normally.
    //
    // The cache is rebuilt whenever the layer's contents change (DrawList.version: placing, breaking or
    // removing blocks changes membership, which is also when tiles re-tile their edges) or the level
    // changes, and it's bypassed in the editor, online, with collision display and during virtual
    // (wireframe) effects, which depend on per-sprite geometry.
    public static class StaticLayerCache
    {
        private const int MinimumStaticThings = 32;
        private const int MaximumTargetSize = 4096;
        private const float Margin = 16f;

        private sealed class Entry
        {
            public Level level;
            public HashSet<Thing> source;
            public int version = -1;
            public bool enabled;
            public RenderTarget2D target;
            public Vec2 origin;
            public float depth;
            public readonly HashSet<Thing> dynamicThings = new HashSet<Thing>();
            public readonly List<Thing> staticThings = new List<Thing>();
        }

        private static readonly Dictionary<Layer, Entry> Entries = new Dictionary<Layer, Entry>();

        private static bool Applies(Layer layer) =>
            (layer == Layer.Blocks || layer == Layer.Background)
            && !Network.isActive
            && !(Level.current is Editor)
            && !DevConsole.showCollision
            && !Layer.doVirtualEffect
            && !VirtualTransition.active
            && !layer.perspective;

        private static bool IsStatic(Thing thing)
        {
            if (!(thing is BackgroundTile || thing is AutoBlock) || !thing.visible || thing.material != null || thing.owner != null)
                return false;
            // Animated tiles must keep updating.
            return !(thing.graphic is SpriteMap map) || map.currentAnimation == "" || map.animations.Count <= 1;
        }

        // Called by Layer.Draw before the layer's batch begins, so the target can be (re)drawn.
        public static void Prepare(Layer layer, HashSet<Thing> things, int version)
        {
            if (!Applies(layer))
                return;
            if (!Entries.TryGetValue(layer, out Entry entry))
                Entries[layer] = entry = new Entry();
            if (entry.level == Level.current && entry.source == things && entry.version == version)
                return;

            entry.level = Level.current;
            entry.source = things;
            entry.version = version;
            Rebuild(layer, entry);
        }

        private static void Rebuild(Layer layer, Entry entry)
        {
            entry.enabled = false;
            entry.staticThings.Clear();
            entry.dynamicThings.Clear();

            float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
            float depth = float.MaxValue;
            foreach (Thing thing in entry.source)
            {
                if (IsStatic(thing))
                {
                    entry.staticThings.Add(thing);
                    left = Math.Min(left, thing.left);
                    top = Math.Min(top, thing.top);
                    right = Math.Max(right, thing.right);
                    bottom = Math.Max(bottom, thing.bottom);
                    depth = Math.Min(depth, thing.depth.value);
                }
                else
                {
                    entry.dynamicThings.Add(thing);
                }
            }
            if (entry.staticThings.Count < MinimumStaticThings)
                return;

            left = (float)Math.Floor(left - Margin);
            top = (float)Math.Floor(top - Margin);
            int width = (int)Math.Ceiling(right + Margin - left);
            int height = (int)Math.Ceiling(bottom + Margin - top);
            if (width > MaximumTargetSize || height > MaximumTargetSize)
                return;

            if (entry.target == null || entry.target.width != width || entry.target.height != height)
            {
                entry.target?.Dispose();
                entry.target = new RenderTarget2D(width, height);
            }

            RenderTarget2D previousTarget = Graphics.currentRenderTarget;
            Viewport previousViewport = Graphics.viewport;
            Layer previousLayer = Graphics.currentLayer;
            MTSpriteBatch previousBatch = Graphics.screen;

            Graphics.SetRenderTarget(entry.target);
            Graphics.Clear(new Color(0, 0, 0, 0));
            Graphics.currentLayer = layer;
            Camera camera = new Camera(left, top, width, height);
            Graphics.screen.Begin(SpriteSortMode.FrontToBack, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone, null, camera.getMatrix());
            foreach (Thing thing in entry.staticThings)
            {
                thing.DoDraw();
                Graphics.material = null;
            }
            Graphics.screen.End();

            Graphics.screen = previousBatch;
            Graphics.currentLayer = previousLayer;
            Graphics.SetRenderTarget(previousTarget);
            Graphics.viewport = previousViewport;

            entry.origin = new Vec2(left, top);
            entry.depth = depth;
            entry.enabled = true;
        }

        // Called inside the layer's batch: draws the cached tiles and returns the things still to draw.
        public static HashSet<Thing> Draw(Layer layer, HashSet<Thing> things)
        {
            if (!Applies(layer) || !Entries.TryGetValue(layer, out Entry entry) || !entry.enabled || entry.source != things)
                return things;
            foreach (Thing thing in entry.staticThings)
                thing.currentlyDrawing = true;
            Graphics.Draw(entry.target, entry.origin, null, Color.White, 0f, Vec2.Zero, Vec2.One, SpriteEffects.None, (Depth)entry.depth);
            return entry.dynamicThings;
        }
    }
}
