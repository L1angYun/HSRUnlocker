using System;
using System.Collections.Generic;

namespace StarRailUnlocker.Core
{
    public enum MobileSafeAreaMode { Original = 0, Custom = 1 }

    internal static class MobileSafeArea
    {
        internal const string GetterName = "UnityEngine.Screen::get_safeArea_Injected";

        internal static GameBuildResolver.NamedRegistration ResolveRegistration(PeImage image,
            MobileSafeAreaMode mode, double horizontalPercent)
        {
            if (mode != MobileSafeAreaMode.Custom || horizontalPercent == 0) return null;
            if (!double.IsFinite(horizontalPercent) || horizontalPercent < 0 || horizontalPercent > 15)
                throw new ArgumentOutOfRangeException(nameof(horizontalPercent));
            ArgumentNullException.ThrowIfNull(image);
            return GameBuildResolver.ResolveNamedRegistration(image, GetterName);
        }

        internal static byte[] BuildGetter(ulong original, double horizontalPercent)
        {
            if (original == 0) throw new ArgumentOutOfRangeException(nameof(original));
            if (!double.IsFinite(horizontalPercent) || horizontalPercent < 0 || horizontalPercent > 15)
                throw new ArgumentOutOfRangeException(nameof(horizontalPercent));
            var code = new List<byte>(256);
            var exits = new List<int>();
            void Emit(params byte[] bytes) => code.AddRange(bytes);
            void Exit(byte condition)
            {
                Emit(0x0F, condition);
                exits.Add(code.Count);
                Emit(0, 0, 0, 0);
            }
            void CheckFinite()
            {
                Emit(0x41, 0x81, 0xE2, 0, 0, 0x80, 0x7F); // and r10d, float exponent
                Emit(0x41, 0x81, 0xFA, 0, 0, 0x80, 0x7F);
                Exit(0x84);
            }

            // Keep the Rect pointer across the original call, with Win64 shadow space.
            Emit(0x53, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x89, 0xCB, 0x48, 0xB8);
            code.AddRange(BitConverter.GetBytes(original));
            Emit(0xFF, 0xD0);
            if (horizontalPercent != 0)
            {
                foreach (byte offset in new byte[] { 0, 4, 8, 12 })
                {
                    Emit(0x44, 0x8B, 0x53, offset); // mov r10d,[rbx+offset]
                    CheckFinite();
                }
                Emit(0x83, 0x7B, 0x08, 0);
                Exit(0x8E); // nonpositive width
                Emit(0x83, 0x7B, 0x0C, 0);
                Exit(0x8E); // nonpositive height
                Emit(0xF3, 0x0F, 0x10, 0x43, 0x08); // xmm0 = original width
                Emit(0x41, 0xBA);
                code.AddRange(BitConverter.GetBytes((float)(horizontalPercent / 100)));
                Emit(0x66, 0x41, 0x0F, 0x6E, 0xCA); // xmm1 = fraction
                Emit(0xF3, 0x0F, 0x59, 0xC8); // margin = width * fraction
                Emit(0xF3, 0x0F, 0x10, 0x13, 0xF3, 0x0F, 0x58, 0xD1); // xmm2 = x + margin
                Emit(0xF3, 0x0F, 0x58, 0xC9, 0xF3, 0x0F, 0x5C, 0xC1); // width -= 2 * margin
                Emit(0x66, 0x41, 0x0F, 0x7E, 0xD2); // validate staged x before either store
                CheckFinite();
                Emit(0x66, 0x41, 0x0F, 0x7E, 0xC3, 0x45, 0x85, 0xDB);
                Exit(0x8E);
                Emit(0xF3, 0x0F, 0x11, 0x13, 0xF3, 0x0F, 0x11, 0x43, 0x08);
            }
            int epilogue = code.Count;
            Emit(0x48, 0x83, 0xC4, 0x20, 0x5B, 0xC3);
            foreach (int at in exits)
            {
                byte[] displacement = BitConverter.GetBytes(checked(epilogue - (at + 4)));
                for (int i = 0; i < 4; i++) code[at + i] = displacement[i];
            }
            return code.ToArray();
        }

        internal static byte[] BuildUnwindInfo() =>
            new byte[] { 0x01, 0x05, 0x02, 0x00, 0x05, 0x32, 0x01, 0x30 };
    }
}
