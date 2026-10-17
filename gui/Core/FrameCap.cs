using System;

namespace StarRailUnlocker.Core
{
    /// <summary>
    /// Resolves Application.targetFrameRate through its actual IL2CPP setter and getter.
    /// Both must reference the same writable static. No value-based candidate guessing.
    /// </summary>
    public static class FrameCapLocator
    {
        public sealed class Candidate
        {
            public ulong Rva;
            public int LiveValue;
        }

        public static Candidate Resolve(TargetProcess proc, ResolvedGameBuild build, Action<string> log = null)
        {
            ulong setter = build.FrameSetterTarget;
            ulong getter = build.FrameGetterTarget;
            ulong Follow(ulong pointer)
            {
                for (int depth = 0; depth < 4; depth++)
                {
                    build.VerifyNativeCode(proc, pointer);
                    var code = proc.Read(pointer, 16);
                    if (code == null) throw new InvalidOperationException("无法读取帧率接口代码");
                    if (code[0] != 0xE9) return pointer;
                    pointer = checked((ulong)((long)pointer + 5 + BitConverter.ToInt32(code, 1)));
                }
                throw new NotSupportedException("帧率接口的跳转层数过多");
            }
            setter = Follow(setter);
            getter = Follow(getter);
            var setCode = proc.Read(setter, 16);
            var getCode = proc.Read(getter, 16);
            if (setCode == null || setCode[0] != 0x89 || setCode[1] != 0x0D || setCode[6] != 0xE9)
                throw new NotSupportedException("当前客户端的帧率 setter 结构不受支持");
            if (getCode == null || getCode[0] != 0x8B || getCode[1] != 0x05 || getCode[6] != 0xC3)
                throw new NotSupportedException("当前客户端的帧率 getter 结构不受支持");
            ulong setStatic = checked((ulong)((long)setter + 6 + BitConverter.ToInt32(setCode, 2)));
            ulong getStatic = checked((ulong)((long)getter + 6 + BitConverter.ToInt32(getCode, 2)));
            if (setStatic != getStatic || setStatic < build.UnityBase
                || !build.UnityImage.IsWritableRva(setStatic - build.UnityBase, 4))
                throw new InvalidOperationException("帧率 setter 与 getter 未指向同一个可写静态量");
            int live = proc.ReadInt32(setStatic);
            if (live == int.MinValue) throw new InvalidOperationException("无法读取帧率静态量");
            var result = new Candidate { Rva = setStatic - build.UnityBase, LiveValue = live };
            log?.Invoke($"帧率 getter/setter 一致指向 UnityPlayer+0x{result.Rva:X}，当前值 {live}");
            return result;
        }
    }
}
