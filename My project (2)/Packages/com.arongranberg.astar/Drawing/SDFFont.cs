using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Pathfinding.Drawing
{
    namespace Text
    {
        /// <summary>Represents a single character in a font texture</summary>
        internal struct SDFCharacter
        {
            public char codePoint;
            float2 uvtopleft, uvbottomright;
            float2 vtopleft, vbottomright;
            public float advance;

            public float2 uvTopLeft => uvtopleft;
            public float2 uvTopRight => new float2(uvbottomright.x, uvtopleft.y);
            public float2 uvBottomLeft => new float2(uvtopleft.x, uvbottomright.y);
            public float2 uvBottomRight => uvbottomright;

            public float2 vertexTopLeft => vtopleft;
            public float2 vertexTopRight => new float2(vbottomright.x, vtopleft.y);
            public float2 vertexBottomLeft => new float2(vtopleft.x, vbottomright.y);
            public float2 vertexBottomRight => vbottomright;

            public SDFCharacter(char codePoint, int x, int y, int width, int height, int originX, int originY, int advance, int textureWidth, int textureHeight, float defaultSize)
            {
                float2 texSize = new float2(textureWidth, textureHeight);

                this.codePoint = codePoint;
                var uvMin = new float2(x, y) / texSize;
                var uvMax = new float2(x + width, y + height) / texSize;

                // UV (0,0) is at the bottom-left in Unity
                uvtopleft = new float2(uvMin.x, 1.0f - uvMin.y);
                uvbottomright = new float2(uvMax.x, 1.0f - uvMax.y);

                var pivot = new float2(-originX, originY);
                this.vtopleft = (pivot + new float2(0, 0)) / defaultSize;
                this.vbottomright = (pivot + new float2(width, -height)) / defaultSize;
                this.advance = advance / defaultSize;
            }
        }

        /// <summary>Represents an SDF font</summary>
        internal struct SDFFont
        {
            public string name;
            public int size, width, height;
            public bool bold, italic;
            public SDFCharacter[] characters;
            public UnityEngine.Material material;
        }

        /// <summary>Optimzed lookup for accessing font data from the unity job system</summary>
        internal struct SDFLookupData
        {
            public NativeArray<SDFCharacter> characters;
            Dictionary<char, int> lookup;
            public Material material;

            public const System.UInt16 Newline = System.UInt16.MaxValue;

            public SDFLookupData(SDFFont font)
            {
                // Create a native array with the character data.
                // Note that the 'char' type is non-blittable in C# and this is required
                // for the NativeArray constructor that takes a T[] to copy.
                // However native arrays can store 'char's, so we copy them one by one instead.
                int nonAscii = 0;
                SDFCharacter questionMark = font.characters[0];

                for (int i = 0; i < font.characters.Length; i++)
                {
                    if (font.characters[i].codePoint == '?')
                    {
                        questionMark = font.characters[i];
                    }
                    if (font.characters[i].codePoint >= 128)
                    {
                        nonAscii++;
                    }
                }
                characters = new NativeArray<SDFCharacter>(128 + nonAscii, Allocator.Persistent);
                for (int i = 0; i < characters.Length; i++)
                {
                    characters[i] = questionMark;
                }
                lookup = new Dictionary<char, int>();
                material = font.material;

                nonAscii = 0;
                for (int i = 0; i < font.characters.Length; i++)
                {
                    var sdfChar = font.characters[i];
                    int targetIndex = sdfChar.codePoint;
                    if (sdfChar.codePoint >= 128)
                    {
                        targetIndex = 128 + nonAscii;
                        nonAscii++;
                    }
                    characters[targetIndex] = sdfChar;
                    lookup[sdfChar.codePoint] = targetIndex;
                }
            }

            public int GetIndex(char c)
            {
                if (lookup.TryGetValue(c, out int index))
                {
                    return index;
                }
                else
                {
                    if (c == '\n') return Newline;
                    return lookup['?'];
                }
            }

            public void Dispose()
            {
                if (characters.IsCreated)
                {
                    characters.Dispose();
                }
            }
        }

        static class DefaultFonts
        {
            internal static SDFFont LoadDefaultFont()
            {
                var font = new SDFFont
                {
                    name = "Droid Sans Mono",
                    size = 32,
                    bold = false,
                    italic = false,
                    width = 1024,
                    height = 128,
                    characters = null,
                    material = UnityEngine.Resources.Load<UnityEngine.Material>("aline_droid_sans_mono")
                };

                // Generated by https://evanw.github.io/font-texture-generator/
                SDFCharacter[] characters_Droid_Sans_Mono = {
                    new SDFCharacter(' ', 414, 79, 12, 12, 6, 6, 19, font.width, font.height, font.size),
                    new SDFCharacter('!', 669, 44, 16, 35, -2, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('"', 258, 79, 23, 20, 2, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('#', 919, 0, 30, 35, 5, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('$', 231, 0, 26, 38, 3, 30, 19, font.width, font.height, font.size),
                    new SDFCharacter('%', 393, 0, 31, 36, 6, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('&', 424, 0, 31, 36, 5, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('\'', 281, 79, 16, 20, -2, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('(', 115, 0, 22, 40, 1, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter(')', 137, 0, 22, 40, 1, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('*', 159, 79, 27, 26, 4, 30, 19, font.width, font.height, font.size),
                    new SDFCharacter('+', 186, 79, 27, 26, 4, 24, 19, font.width, font.height, font.size),
                    new SDFCharacter(',', 240, 79, 18, 21, -1, 10, 19, font.width, font.height, font.size),
                    new SDFCharacter('-', 359, 79, 23, 15, 2, 16, 19, font.width, font.height, font.size),
                    new SDFCharacter('.', 315, 79, 17, 17, -1, 11, 19, font.width, font.height, font.size),
                    new SDFCharacter('/', 500, 44, 25, 35, 3, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('0', 569, 0, 27, 36, 4, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('1', 649, 44, 20, 35, 2, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('2', 313, 44, 27, 35, 4, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('3', 758, 0, 26, 36, 4, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('4', 60, 44, 29, 35, 5, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('5', 448, 44, 26, 35, 3, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('6', 596, 0, 27, 36, 4, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('7', 340, 44, 27, 35, 4, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('8', 623, 0, 27, 36, 4, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('9', 650, 0, 27, 36, 4, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter(':', 861, 44, 16, 30, -2, 23, 19, font.width, font.height, font.size),
                    new SDFCharacter(';', 711, 44, 18, 34, 0, 23, 19, font.width, font.height, font.size),
                    new SDFCharacter('<', 77, 79, 27, 28, 4, 25, 19, font.width, font.height, font.size),
                    new SDFCharacter('=', 213, 79, 27, 21, 4, 22, 19, font.width, font.height, font.size),
                    new SDFCharacter('>', 104, 79, 27, 28, 4, 25, 19, font.width, font.height, font.size),
                    new SDFCharacter('?', 784, 0, 26, 36, 3, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('@', 200, 0, 31, 38, 6, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('A', 949, 0, 30, 35, 5, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('B', 89, 44, 28, 35, 4, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('C', 513, 0, 28, 36, 4, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('D', 117, 44, 28, 35, 4, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('E', 474, 44, 26, 35, 3, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('F', 525, 44, 25, 35, 2, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('G', 541, 0, 28, 36, 4, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('H', 367, 44, 27, 35, 4, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('I', 625, 44, 24, 35, 2, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('J', 550, 44, 25, 35, 4, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('K', 145, 44, 28, 35, 3, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('L', 575, 44, 25, 35, 2, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('M', 173, 44, 28, 35, 4, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('N', 394, 44, 27, 35, 4, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('O', 455, 0, 29, 36, 5, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('P', 421, 44, 27, 35, 3, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('Q', 38, 0, 29, 42, 5, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('R', 201, 44, 28, 35, 3, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('S', 677, 0, 27, 36, 4, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('T', 229, 44, 28, 35, 4, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('U', 257, 44, 28, 35, 4, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('V', 979, 0, 30, 35, 5, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('W', 888, 0, 31, 35, 6, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('X', 0, 44, 30, 35, 5, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('Y', 30, 44, 30, 35, 5, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('Z', 285, 44, 28, 35, 4, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('[', 159, 0, 21, 40, 0, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('\\', 600, 44, 25, 35, 3, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter(']', 180, 0, 20, 40, 1, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('^', 131, 79, 28, 26, 4, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('_', 382, 79, 32, 14, 6, 3, 19, font.width, font.height, font.size),
                    new SDFCharacter('`', 297, 79, 18, 17, -1, 31, 19, font.width, font.height, font.size),
                    new SDFCharacter('a', 784, 44, 26, 30, 4, 23, 19, font.width, font.height, font.size),
                    new SDFCharacter('b', 285, 0, 27, 37, 4, 30, 19, font.width, font.height, font.size),
                    new SDFCharacter('c', 810, 44, 26, 30, 3, 23, 19, font.width, font.height, font.size),
                    new SDFCharacter('d', 312, 0, 27, 37, 4, 30, 19, font.width, font.height, font.size),
                    new SDFCharacter('e', 757, 44, 27, 30, 4, 23, 19, font.width, font.height, font.size),
                    new SDFCharacter('f', 704, 0, 27, 36, 4, 30, 19, font.width, font.height, font.size),
                    new SDFCharacter('g', 257, 0, 28, 37, 4, 23, 19, font.width, font.height, font.size),
                    new SDFCharacter('h', 810, 0, 26, 36, 3, 30, 19, font.width, font.height, font.size),
                    new SDFCharacter('i', 836, 0, 26, 36, 3, 30, 19, font.width, font.height, font.size),
                    new SDFCharacter('j', 0, 0, 23, 44, 4, 30, 19, font.width, font.height, font.size),
                    new SDFCharacter('k', 731, 0, 27, 36, 3, 30, 19, font.width, font.height, font.size),
                    new SDFCharacter('l', 862, 0, 26, 36, 3, 30, 19, font.width, font.height, font.size),
                    new SDFCharacter('m', 909, 44, 29, 29, 5, 23, 19, font.width, font.height, font.size),
                    new SDFCharacter('n', 995, 44, 26, 29, 3, 23, 19, font.width, font.height, font.size),
                    new SDFCharacter('o', 729, 44, 28, 30, 4, 23, 19, font.width, font.height, font.size),
                    new SDFCharacter('p', 339, 0, 27, 37, 4, 23, 19, font.width, font.height, font.size),
                    new SDFCharacter('q', 366, 0, 27, 37, 4, 23, 19, font.width, font.height, font.size),
                    new SDFCharacter('r', 52, 79, 25, 29, 2, 23, 19, font.width, font.height, font.size),
                    new SDFCharacter('s', 836, 44, 25, 30, 3, 23, 19, font.width, font.height, font.size),
                    new SDFCharacter('t', 685, 44, 26, 34, 4, 28, 19, font.width, font.height, font.size),
                    new SDFCharacter('u', 0, 79, 26, 29, 3, 23, 19, font.width, font.height, font.size),
                    new SDFCharacter('v', 938, 44, 29, 29, 5, 23, 19, font.width, font.height, font.size),
                    new SDFCharacter('w', 877, 44, 32, 29, 6, 23, 19, font.width, font.height, font.size),
                    new SDFCharacter('x', 967, 44, 28, 29, 4, 23, 19, font.width, font.height, font.size),
                    new SDFCharacter('y', 484, 0, 29, 36, 5, 23, 19, font.width, font.height, font.size),
                    new SDFCharacter('z', 26, 79, 26, 29, 3, 23, 19, font.width, font.height, font.size),
                    new SDFCharacter('{', 67, 0, 24, 40, 2, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('|', 23, 0, 15, 44, -2, 30, 19, font.width, font.height, font.size),
                    new SDFCharacter('}', 91, 0, 24, 40, 2, 29, 19, font.width, font.height, font.size),
                    new SDFCharacter('~', 332, 79, 27, 16, 4, 19, 19, font.width, font.height, font.size),
                };

                font.characters = characters_Droid_Sans_Mono;

                return font;
            }
        }
    }
}
