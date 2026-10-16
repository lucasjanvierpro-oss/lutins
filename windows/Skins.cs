// Les persos en pixel art et leurs effets : la même chose que Sprites.swift, Creatures.swift et Effects.swift.
// Tout se dessine dans une grille de 14×13 « pixels » ; chaque pixel fait `pixel` pixels d'écran.
// Écrit en C# 5 pour le compilateur livré avec Windows (pas de $"", ?., =>).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;

namespace Lutins
{
    delegate void Dot(int c, int r, Color color);

    struct Px : IEquatable<Px>
    {
        public readonly int C, R;
        public Px(int c, int r) { C = c; R = r; }
        public bool Equals(Px other) { return C == other.C && R == other.R; }
        public override bool Equals(object obj) { return obj is Px && Equals((Px)obj); }
        public override int GetHashCode() { return C * 397 ^ R; }
    }

    struct Look
    {
        public readonly LutinState State;
        public readonly double Age;
        public Look(LutinState state, double age) { State = state; Age = age; }
    }

    static class Colors
    {
        public static Color Hex(int hex)
        {
            return Color.FromArgb(255, (hex >> 16) & 0xFF, (hex >> 8) & 0xFF, hex & 0xFF);
        }

        public static Color Alpha(Color c, double alpha)
        {
            int a = (int)Math.Round(c.A * alpha);
            return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B);
        }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb(255,
                (int)Math.Round(a.R * (1 - t) + b.R * t),
                (int)Math.Round(a.G * (1 - t) + b.G * t),
                (int)Math.Round(a.B * (1 - t) + b.B * t));
        }
    }

    static class Effects
    {
        public const double FrameDuration = 0.12;
        /// Petits sauts pendant le travail, gros saut quand c'est fini.
        public static readonly int[] Hop = { 0, -1, -2, -2, -1, 0 };
        public static readonly int[] BigJump = { 0, -2, -3, -3, -2, 0 };

        public static readonly Color BlueGlow = Colors.Hex(0x80C2FF);
        public static readonly Color GreenGlow = Colors.Hex(0x6BF299);
        static readonly Color[] ConfettiColors =
        {
            Colors.Hex(0x34C759), Colors.Hex(0xFFCC00), Colors.Hex(0xFF2D55), Colors.Hex(0x30B0C7), Colors.Hex(0xFFFFFF),
        };

        /// Entre 0 et 1, en boucle sur `period` images.
        public static double Pulse(int frame, double period)
        {
            return 0.5 + 0.5 * Math.Sin(frame * 2 * Math.PI / period);
        }

        /// Le halo qui correspond à l'état : bleu qui pulse au travail, vert léger quand c'est fini.
        public static void Glow(HashSet<Px> shape, LutinState state, int frame, Dot dot)
        {
            if (state == LutinState.Working) Aura(shape, BlueGlow, 0.1 + 0.9 * Pulse(frame, 8), dot);
            else if (state == LutinState.Done) Aura(shape, GreenGlow, 0.35, dot);
        }

        static HashSet<Px> Around(HashSet<Px> set, bool diagonal)
        {
            int[,] steps = diagonal
                ? new int[,] { { -1, -1 }, { 0, -1 }, { 1, -1 }, { -1, 0 }, { 1, 0 }, { -1, 1 }, { 0, 1 }, { 1, 1 } }
                : new int[,] { { 0, -1 }, { -1, 0 }, { 1, 0 }, { 0, 1 } };
            var result = new HashSet<Px>();
            foreach (var p in set)
                for (int i = 0; i < steps.GetLength(0); i++)
                {
                    var q = new Px(p.C + steps[i, 0], p.R + steps[i, 1]);
                    if (!set.Contains(q)) result.Add(q);
                }
            return result;
        }

        /// Halo en pixels autour de la silhouette : un anneau franc, puis un anneau léger.
        public static void Aura(HashSet<Px> shape, Color color, double strength, Dot dot)
        {
            var inner = Around(shape, true);
            var both = new HashSet<Px>(shape);
            both.UnionWith(inner);
            var outer = Around(both, false);
            foreach (var p in outer) dot(p.C, p.R, Colors.Alpha(color, 0.3 * strength));
            foreach (var p in inner) dot(p.C, p.R, Colors.Alpha(color, 0.9 * strength));
        }

        /// Gerbe de confettis pendant les 2 premières secondes après la fin, partant de `origin`.
        public static void Confetti(double age, Px origin, Dot dot)
        {
            double k = age / FrameDuration;
            if (k >= 16) return;
            for (int i = 0; i < 18; i++)
            {
                double angle = i / 18.0 * 2 * Math.PI;
                double speed = 0.4 + ((i * 37) % 10) / 20.0;
                double x = origin.C + Math.Cos(angle) * speed * k;
                double y = origin.R - (Math.Abs(Math.Sin(angle)) * speed + 0.3) * k + 0.06 * k * k;
                dot(Round(x), Round(y), ConfettiColors[i % ConfettiColors.Length]);
            }
        }

        public static int Round(double v) { return (int)Math.Round(v, MidpointRounding.AwayFromZero); }
    }

    /// Le lutin de base : sa couleur dit l'état.
    static class Sprite
    {
        static readonly string[] Body =
        {
            "..####..",
            ".######.",
            "########",
            "########",
            "########",
            "########",
            ".######.",
        };

        public static Color StateColor(LutinState state)
        {
            switch (state)
            {
                case LutinState.Working: return Colors.Hex(0x4C8DF6);
                case LutinState.Waiting: return Colors.Hex(0xFF7A1A);
                case LutinState.Done: return Colors.Hex(0x2ECC71);
                default: return Colors.Hex(0x9AA0A6);
            }
        }

        public static void Draw(Look look, int frame, Dot dot, bool sleepyZ, bool effects)
        {
            Color baseColor = StateColor(look.State);
            Color dark = Colors.Mix(baseColor, Color.Black, 0.35);
            Color light = Colors.Mix(baseColor, Color.White, 0.55);
            Color ink = Colors.Hex(0x17171F);

            int dx = 0, dy = 0, tip = 4;
            string legs = "..#..#..";
            int[][] eyes = { new[] { 2, 3 }, new[] { 2, 4 }, new[] { 5, 3 }, new[] { 5, 4 } };
            int[][] mouth = new int[0][];
            bool bang = false, zzz = false;

            switch (look.State)
            {
                case LutinState.Working:
                    dy = Effects.Hop[frame % Effects.Hop.Length];
                    if (dy == 0 && (frame / Effects.Hop.Length) % 2 == 1) legs = ".#....#.";
                    int glance = new[] { 0, -1, 0, 1 }[(frame / 10) % 4];
                    eyes = eyes.Select(e => new[] { e[0] + glance, e[1] }).ToArray();
                    tip = frame % 2 == 0 ? 3 : 5;
                    break;
                case LutinState.Waiting:
                    eyes = new[] { new[] { 2, 2 }, new[] { 2, 3 }, new[] { 5, 2 }, new[] { 5, 3 } };
                    mouth = new[] { new[] { 3, 5 }, new[] { 4, 5 } };
                    bang = (frame / 3) % 2 == 0;
                    dx = new[] { 0, 1, 0, -1 }[frame % 4];
                    break;
                case LutinState.Done:
                    eyes = new[] { new[] { 2, 3 }, new[] { 5, 3 } };
                    mouth = new[] { new[] { 2, 4 }, new[] { 3, 5 }, new[] { 4, 5 }, new[] { 5, 4 } };
                    if (look.Age < 3) dy = Effects.BigJump[frame % Effects.BigJump.Length];
                    break;
                default:
                    eyes = new[] { new[] { 1, 4 }, new[] { 2, 4 }, new[] { 5, 4 }, new[] { 6, 4 } };
                    zzz = sleepyZ && (frame / 8) % 2 == 0;
                    break;
            }

            int bx = 1 + dx, by = 4 + dy;
            var pixels = new List<KeyValuePair<Px, Color>>();
            for (int r = 0; r < Body.Length; r++)
                for (int c = 0; c < Body[r].Length; c++)
                    if (Body[r][c] == '#') pixels.Add(new KeyValuePair<Px, Color>(new Px(bx + c, by + r), baseColor));
            foreach (var e in eyes.Concat(mouth)) pixels.Add(new KeyValuePair<Px, Color>(new Px(bx + e[0], by + e[1]), ink));
            pixels.Add(new KeyValuePair<Px, Color>(new Px(bx + 4, by - 1), baseColor));
            pixels.Add(new KeyValuePair<Px, Color>(new Px(bx + tip, by - 2), light));
            for (int c = 0; c < legs.Length; c++)
                if (legs[c] == '#') pixels.Add(new KeyValuePair<Px, Color>(new Px(bx + c, by + 7), dark));

            if (effects) Effects.Glow(new HashSet<Px>(pixels.Select(p => p.Key)), look.State, frame, dot);
            foreach (var p in pixels) dot(p.Key.C, p.Key.R, p.Value);
            if (bang) foreach (int r in new[] { 0, 1, 2, 4 }) dot(9, r, baseColor);
            if (zzz)
                foreach (var z in new[] { new[] { 7, 0 }, new[] { 8, 0 }, new[] { 9, 0 }, new[] { 8, 1 }, new[] { 7, 2 }, new[] { 7, 3 }, new[] { 8, 3 }, new[] { 9, 3 } })
                    dot(z[0], z[1], baseColor);
            if (effects && look.State == LutinState.Done) Effects.Confetti(look.Age, new Px(5, 3), dot);
        }
    }

    /// Une créature : dessin de 12×10 avec sa palette. L'état se lit au halo et aux petits signes.
    sealed class Creature
    {
        public string Id, Name;
        public Dictionary<char, Color> Palette;
        /// 10 lignes de 12 caractères ; « . » = transparent. La dernière ligne, ce sont les pieds.
        public string[] Art;
        /// Pieds pour l'autre pas de la marche.
        public string Walk;
        /// Pixels des yeux (colonne, ligne), et couleur de paupière pour les fermer.
        public int[][] Eyes;
        public char Lid;
        public bool Floats;

        public void Draw(Look look, int frame, Dot dot, bool sleepyZ, bool decorated, bool effects)
        {
            int dx = 0, dy = 0;
            bool stepping = false;
            switch (look.State)
            {
                case LutinState.Working:
                    dy = Effects.Hop[frame % Effects.Hop.Length];
                    stepping = dy == 0 && (frame / Effects.Hop.Length) % 2 == 1;
                    break;
                case LutinState.Waiting:
                    dx = new[] { 0, 1, 0, -1 }[frame % 4];
                    break;
                case LutinState.Done:
                    if (look.Age < 3) dy = Effects.BigJump[frame % Effects.BigJump.Length];
                    break;
                default:
                    if (Floats) dy = (frame / 8) % 2 == 0 ? 0 : -1;
                    break;
            }

            int ox = dx, oy = 2 + dy;
            var shape = new HashSet<Px>();
            for (int r = 0; r < Art.Length; r++)
            {
                string row = stepping && r == Art.Length - 1 ? Walk : Art[r];
                for (int c = 0; c < row.Length; c++)
                    if (row[c] != '.') shape.Add(new Px(ox + c, oy + r));
            }
            if (decorated && effects) Effects.Glow(shape, look.State, frame, dot);

            // Halo au sol : la couleur de l'état.
            if (decorated && look.State != LutinState.Idle)
            {
                Color glow = Sprite.StateColor(look.State);
                for (int c = 1; c <= 10; c++) dot(c, 12, Colors.Alpha(glow, c == 1 || c == 10 ? 0.45 : 0.95));
            }

            for (int r = 0; r < Art.Length; r++)
            {
                string row = stepping && r == Art.Length - 1 ? Walk : Art[r];
                for (int c = 0; c < row.Length; c++)
                {
                    Color color;
                    if (row[c] != '.' && Palette.TryGetValue(row[c], out color)) dot(ox + c, oy + r, color);
                }
            }

            // Yeux fermés quand il dort, et un clignement de temps en temps.
            bool blink = look.State != LutinState.Idle && frame % 37 == 36;
            Color lidColor, ink;
            if ((look.State == LutinState.Idle || blink) && Palette.TryGetValue(Lid, out lidColor)
                && (Palette.TryGetValue('K', out ink) || Palette.TryGetValue('E', out ink)))
            {
                const double center = 5.5;
                var sides = new[] { Eyes.Where(e => e[0] < center).ToArray(), Eyes.Where(e => e[0] >= center).ToArray() };
                foreach (var side in sides)
                {
                    if (side.Length == 0) continue;
                    foreach (var e in side) dot(ox + e[0], oy + e[1], lidColor);
                    int bottom = side.Max(e => e[1]);
                    var cols = side.Select(e => e[0]).ToList();
                    int outward = cols[0] < center ? cols.Min() - 1 : cols.Max() + 1;
                    foreach (int c in new HashSet<int>(cols.Concat(new[] { outward }))) dot(ox + c, oy + bottom, ink);
                }
            }

            if (!decorated) return;
            Color stateColor = Sprite.StateColor(look.State);
            if (look.State == LutinState.Waiting && (frame / 3) % 2 == 0)
            {
                foreach (int r in new[] { 1, 2, 3, 5 }) dot(12, r, stateColor);
            }
            else if (look.State == LutinState.Done && ((frame / 4) % 2 == 0 || look.Age >= 3))
            {
                foreach (var p in new[] { new[] { 12, 0 }, new[] { 11, 1 }, new[] { 12, 1 }, new[] { 13, 1 }, new[] { 12, 2 } })
                    dot(p[0], p[1], stateColor);
            }
            else if (look.State == LutinState.Idle && sleepyZ && (frame / 8) % 2 == 0)
            {
                foreach (var p in new[] { new[] { 11, 0 }, new[] { 12, 0 }, new[] { 13, 0 }, new[] { 12, 1 }, new[] { 11, 2 }, new[] { 11, 3 }, new[] { 12, 3 }, new[] { 13, 3 } })
                    dot(p[0], p[1], Colors.Hex(0x9AA0A6));
            }
            if (effects && look.State == LutinState.Done) Effects.Confetti(look.Age, new Px(6, 2), dot);
        }
    }

    static class Skins
    {
        public const int Cols = 14, Rows = 13;
        public const string Mix = "mix";

        static Dictionary<char, Color> P(params object[] pairs)
        {
            var d = new Dictionary<char, Color>();
            for (int i = 0; i < pairs.Length; i += 2) d[(char)pairs[i]] = Colors.Hex((int)pairs[i + 1]);
            return d;
        }

        static int[][] E(params int[] flat)
        {
            var list = new List<int[]>();
            for (int i = 0; i < flat.Length; i += 2) list.Add(new[] { flat[i], flat[i + 1] });
            return list.ToArray();
        }

        public static readonly Creature[] Creatures =
        {
            new Creature
            {
                Id = "panda-roux", Name = "Panda roux",
                Palette = P('O', 0xD9622B, 'o', 0x9E3F17, 'W', 0xF6EDE1, 'D', 0x3A1D10, 'E', 0x140904, 'R', 0xF0A060),
                Art = new[]
                {
                    ".OO....OO...",
                    "ODOOOOOODO..",
                    "OOWOOOOWOO..",
                    "OOEOOOOEOO..",
                    "OOoWEEWoOO..",
                    ".OOOWWOOO..R",
                    "..DOOOOD..oR",
                    "..DDDDDD.oR.",
                    "..DDDDDDRo..",
                    "..DD..DD....",
                },
                Walk = ".DD....DD...", Eyes = E(2, 3, 7, 3), Lid = 'O',
            },
            new Creature
            {
                Id = "koala", Name = "Koala",
                Palette = P('G', 0x9AA3AD, 'g', 0x6F7882, 'W', 0xEEF0F2, 'N', 0x2B2D33, 'E', 0x15161A, 'L', 0x6BCB77),
                Art = new[]
                {
                    ".gg....gg...",
                    "gWWg..gWWg..",
                    "gWGGGGGGWg..",
                    ".GEGGGGEG...",
                    ".GGGNNGGG...",
                    ".GGNNNNGG...",
                    "..GGNNGG.L..",
                    "..GWWWWGL...",
                    "..GWWWWG....",
                    "..gg..gg....",
                },
                Walk = ".gg....gg...", Eyes = E(2, 3, 7, 3), Lid = 'G',
            },
            new Creature
            {
                Id = "kitsune", Name = "Renard magique",
                Palette = P('F', 0xF28A30, 'W', 0xFFF3E2, 'E', 0x1C0F07, 'B', 0x4A2A18, 'G', 0x7FDBFF),
                Art = new[]
                {
                    ".F....F.....",
                    "FFF..FFF....",
                    "FFFFFFFF....",
                    "FEFFFFEF...G",
                    "WWFFFFWW..GG",
                    ".WWEEWW...WG",
                    "..FFFF...FF.",
                    ".FWWWWF.FF..",
                    ".FWWWWFFF...",
                    ".BB..BB.....",
                },
                Walk = "BB....BB....", Eyes = E(1, 3, 6, 3), Lid = 'F',
            },
            new Creature
            {
                Id = "dragon", Name = "Petit dragon",
                Palette = P('G', 0x45C486, 'g', 0x2B8C5C, 'Y', 0xF7DC79, 'H', 0xF4E6C0, 'E', 0x0F1A14),
                Art = new[]
                {
                    "..H....H....",
                    "..HGGGGH....",
                    "..GGGGGG....",
                    "..GEGGEG....",
                    "g.GGggGG.g..",
                    "gg.GYYG.gg..",
                    "gggGYYGggg..",
                    ".ggGYYGgg.Gg",
                    "...GGGGGGG..",
                    "...GG..GG...",
                },
                Walk = "..GG....GG..", Eyes = E(3, 3, 6, 3), Lid = 'G',
            },
            new Creature
            {
                Id = "fantome", Name = "Fantôme",
                Palette = P('W', 0xEEF1FF, 'E', 0x1E2340, 'P', 0xFF9EC7),
                Art = new[]
                {
                    "....WWWW....",
                    "...WWWWWW...",
                    "..WWWWWWWW..",
                    "..WEWWWWEW..",
                    "..WEWWWWEW..",
                    ".WWPWWWWPWW.",
                    "..WWWWWWWW..",
                    "..WWWWWWWW..",
                    "..WWWWWWWW..",
                    "..WW.WW.WW..",
                },
                Walk = "..W.WW.WW.W.", Eyes = E(3, 3, 3, 4, 8, 3, 8, 4), Lid = 'W', Floats = true,
            },
            new Creature
            {
                Id = "chat-sorcier", Name = "Chat sorcier",
                Palette = P('H', 0x8B5CF6, 'S', 0xFFE066, 'C', 0x6B6F9E, 'E', 0xFFE066, 'N', 0xF49AC1, 'K', 0x2A2540),
                Art = new[]
                {
                    ".....HH.....",
                    "....HHHS....",
                    "...HHHHH....",
                    "HHHHHHHHHH..",
                    ".CCCCCCCC...",
                    ".CECCCCEC...",
                    ".CCCNNCCC.C.",
                    "..CCCCCC..C.",
                    "..CCCCCCCC..",
                    "..CC..CC....",
                },
                Walk = ".CC....CC...", Eyes = E(2, 5, 7, 5), Lid = 'C',
            },
            new Creature
            {
                Id = "axolotl", Name = "Axolotl",
                Palette = P('P', 0xFFA3CF, 'p', 0xF25C9A, 'E', 0x2A0F1E, 'M', 0xC23A78),
                Art = new[]
                {
                    "p.p......p.p",
                    ".pPPPPPPPPp.",
                    "pPPPPPPPPPPp",
                    ".PEPPPPPPEP.",
                    "pPPPPPPPPPPp",
                    ".PPPMPPMPPP.",
                    "..PPPMMPPP..",
                    "...PPPPPP...",
                    "...PPPPPP...",
                    "...PP..PP...",
                },
                Walk = "..PP....PP..", Eyes = E(2, 3, 9, 3), Lid = 'P',
            },
            new Creature
            {
                Id = "champignon", Name = "Champignon",
                Palette = P('R', 0xE5484D, 'W', 0xFFF5F5, 'S', 0xF5E6C8, 'E', 0x2A1A10, 'P', 0xFFB0A8),
                Art = new[]
                {
                    "....RRRR....",
                    "..RRWRRRRR..",
                    ".RRWWRRRWRR.",
                    "RRRRRRRWWRRR",
                    "RRWRRRRRRRRR",
                    ".RRRRRRRRRR.",
                    "...SSSSSS...",
                    "...SESSES...",
                    "...PSSSSP...",
                    "...SS..SS...",
                },
                Walk = "..SS....SS..", Eyes = E(4, 7, 7, 7), Lid = 'S',
            },
            new Creature
            {
                Id = "chouette", Name = "Chouette",
                Palette = P('B', 0xA0703F, 'b', 0x6B4423, 'Y', 0xF7D046, 'E', 0x1A120A, 'O', 0xF59A2F, 'C', 0xEBD5AA, 'c', 0xC9A877),
                Art = new[]
                {
                    "..b......b..",
                    "..BBBBBBBB..",
                    ".BYYBBBBYYB.",
                    ".BYEBBBBEYB.",
                    ".BBBBOOBBBB.",
                    "bBCCCCCCCCBb",
                    "bBCcCCCCcCBb",
                    ".BCCCCCCCCB.",
                    "..BBBBBBBB..",
                    "...OO..OO...",
                },
                Walk = "..OO....OO..", Eyes = E(2, 2, 3, 2, 2, 3, 3, 3, 8, 2, 9, 2, 8, 3, 9, 3), Lid = 'B',
            },
        };

        /// Tous les choix possibles, dans l'ordre du menu.
        public static readonly string[] Ids = new[] { "lutin" }.Concat(Creatures.Select(c => c.Id)).ToArray();

        public static string Name(string id)
        {
            if (id == "lutin") return "Lutin (couleur = état)";
            var creature = Creatures.FirstOrDefault(c => c.Id == id);
            return creature != null ? creature.Name : id;
        }

        public static bool IsValid(string choice)
        {
            return choice == Mix || Ids.Contains(choice);
        }

        /// Le perso d'une session : le choix fait, ou en mode mélange un différent par session.
        public static string SkinFor(Session session, string choice)
        {
            return choice == Mix ? Ids[(session.Order - 1) % Ids.Length] : choice;
        }

        public static Size SizeFor(int pixel) { return new Size(Cols * pixel, Rows * pixel); }

        /// Dessine un perso, coin haut-gauche en (x, y).
        public static void Draw(Graphics g, string id, Look look, int frame, int x, int y, int pixel,
                                double alpha = 1, bool sleepyZ = true, bool decorated = true, bool effects = true)
        {
            var creature = Creatures.FirstOrDefault(c => c.Id == id);
            int ox = creature != null ? x : x + pixel * 2;
            int oy = creature != null ? y : y + pixel;
            Dot dot = (c, r, color) =>
            {
                Color final = Colors.Alpha(color, alpha);
                if (final.A == 0) return;
                using (var brush = new SolidBrush(final)) g.FillRectangle(brush, ox + c * pixel, oy + r * pixel, pixel, pixel);
            };
            var smoothing = g.SmoothingMode;
            var offset = g.PixelOffsetMode;
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            if (creature != null) creature.Draw(look, frame, dot, sleepyZ, decorated, effects);
            else Sprite.Draw(look, frame, dot, sleepyZ, effects && decorated);
            g.SmoothingMode = smoothing;
            g.PixelOffsetMode = offset;
        }

        /// Petite image d'un perso, pour le menu et l'icône de la barre des tâches.
        public static Bitmap Icon(string id, int pixel, bool decorated = false)
        {
            var size = SizeFor(pixel);
            var bmp = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                Draw(g, id, new Look(LutinState.Working, 99), 0, 0, 0, pixel, 1, false, decorated, false);
            }
            return bmp;
        }
    }
}
