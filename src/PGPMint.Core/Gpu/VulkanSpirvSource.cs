using System.Numerics;
using System.Text;
using PGPMint.Core.Crypto;

namespace PGPMint.Core.Gpu;

public static class VulkanSpirvSource
{
    public const string KernelVersion = "vulkan-sha1-four-v5";
    public const int DefaultWorkgroupSize = 128;

    private const uint SpirvMagic = 0x07230203u;
    private const uint SpirvVersion13 = 0x00010300u;

    // SPIR-V 1.3 opcodes
    private const uint OpMemoryModel = 14;
    private const uint OpEntryPoint = 15;
    private const uint OpExecutionMode = 16;
    private const uint OpCapability = 17;
    private const uint OpTypeVoid = 19;
    private const uint OpTypeBool = 20;
    private const uint OpTypeInt = 21;
    private const uint OpTypeVector = 23;
    private const uint OpTypeArray = 28;
    private const uint OpTypeRuntimeArray = 29;
    private const uint OpTypeStruct = 30;
    private const uint OpTypePointer = 32;
    private const uint OpTypeFunction = 33;
    private const uint OpConstant = 43;
    private const uint OpFunction = 54;
    private const uint OpFunctionEnd = 56;
    private const uint OpVariable = 59;
    private const uint OpLoad = 61;
    private const uint OpStore = 62;
    private const uint OpAccessChain = 65;
    private const uint OpDecorate = 71;
    private const uint OpMemberDecorate = 72;
    private const uint OpCompositeExtract = 81;
    private const uint OpIAdd = 128;
    private const uint OpIMul = 132;
    private const uint OpLogicalOr = 166;
    private const uint OpSelect = 169;
    private const uint OpIEqual = 170;
    private const uint OpINotEqual = 171;
    private const uint OpULessThan = 176;
    private const uint OpShiftRightLogical = 194;
    private const uint OpShiftLeftLogical = 196;
    private const uint OpBitwiseOr = 197;
    private const uint OpBitwiseXor = 198;
    private const uint OpBitwiseAnd = 199;
    private const uint OpAtomicCompareExchange = 230;
    private const uint OpAtomicIAdd = 234;
    private const uint OpLoopMerge = 246;
    private const uint OpSelectionMerge = 247;
    private const uint OpLabel = 248;
    private const uint OpBranch = 249;
    private const uint OpBranchConditional = 250;
    private const uint OpReturn = 253;

    // SPIR-V storage classes, decorations, and execution modes
    private const uint StorageClassInput = 1;
    private const uint StorageClassUniform = 2;
    private const uint StorageClassFunction = 7;
    private const uint StorageClassPushConstant = 9;
    private const uint StorageClassStorageBuffer = 12;

    private const uint DecorationBlock = 2;
    private const uint DecorationArrayStride = 6;
    private const uint DecorationBuiltIn = 11;
    private const uint DecorationBinding = 33;
    private const uint DecorationDescriptorSet = 34;
    private const uint DecorationOffset = 35;

    private const uint BuiltInGlobalInvocationId = 28;
    private const uint ExecutionModeLocalSize = 17;
    private const uint ExecutionModelGlCompute = 5;
    private const uint CapabilityShader = 1;

    public static byte[] BuildFourKeySpirv(int workgroupSize = DefaultWorkgroupSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workgroupSize);

        uint[] templateFixed = Sha1ScheduleHelper.TemplateFixedSchedule;
        uint[] masks = Sha1ScheduleHelper.TimestampMasks;
        uint[] constants = Sha1ScheduleHelper.PhaseConstants;
        TimestampXorPlan xorPlan = Sha1ScheduleHelper.SharedXorPlan;

        var b = new Builder();
        uint tVoid = b.NewId(); b.EmitType(OpTypeVoid, tVoid);
        uint tFn = b.NewId(); b.EmitType(OpTypeFunction, tFn, tVoid);
        uint tBool = b.NewId(); b.EmitType(OpTypeBool, tBool);
        uint tU32 = b.NewId(); b.EmitType(OpTypeInt, tU32, 32, 0);
        uint tUVec3 = b.NewId(); b.EmitType(OpTypeVector, tUVec3, tU32, 3);
        uint tUVec4 = b.NewId(); b.EmitType(OpTypeVector, tUVec4, tU32, 4);

        uint Cu(uint value) => b.ConstU32(tU32, value);

        // Pre-register constants up front so type section stays well-formed.
        Cu(0u);
        Cu(1u);
        Cu(2u);
        Cu(5u);
        Cu(30u);
        Cu(32u);
        Cu(77u);
        Cu(0xFFFFFFFFu);
        Cu(unchecked(0u - constants[3]));
        Cu(unchecked(0u - 0xC3D2E1F0u));
        Cu(0x67452301u);
        Cu(0xEFCDAB89u);
        Cu(0x98BADCFEu);
        Cu(0x10325476u);
        Cu(0xC3D2E1F0u);
        for (int i = 0; i < constants.Length; i++) Cu(constants[i]);
        for (int i = 0; i < templateFixed.Length; i++) Cu(templateFixed[i]);
        for (uint i = 0; i < 80u; i++) Cu(i);

        uint c0 = Cu(0u);
        uint c1 = Cu(1u);
        uint c77 = Cu(77u);
        uint cAllOnes = Cu(0xFFFFFFFFu);
        uint cNegK3 = Cu(unchecked(0u - constants[3]));

        // Input: GlobalInvocationId
        uint tPtrInUVec3 = b.NewId(); b.EmitType(OpTypePointer, tPtrInUVec3, StorageClassInput, tUVec3);
        uint tPtrInU32 = b.NewId(); b.EmitType(OpTypePointer, tPtrInU32, StorageClassInput, tU32);
        uint vGid = b.NewId(); b.EmitGlobal(OpVariable, tPtrInUVec3, vGid, StorageClassInput);
        b.EmitDecoration(OpDecorate, vGid, DecorationBuiltIn, BuiltInGlobalInvocationId);

        // PushConstant block: 9 explicit uint32 scalars
        uint tPcStruct = b.NewId();
        b.EmitType(OpTypeStruct, tPcStruct, tU32, tU32, tU32, tU32, tU32, tU32, tU32, tU32, tU32);
        b.EmitDecoration(OpDecorate, tPcStruct, DecorationBlock);
        for (uint m = 0; m < 9; m++)
        {
            b.EmitDecoration(OpMemberDecorate, tPcStruct, m, DecorationOffset, m * 4u);
        }
        uint tPtrPcStruct = b.NewId(); b.EmitType(OpTypePointer, tPtrPcStruct, StorageClassPushConstant, tPcStruct);
        uint tPtrPcU32 = b.NewId(); b.EmitType(OpTypePointer, tPtrPcU32, StorageClassPushConstant, tU32);
        uint vPc = b.NewId(); b.EmitGlobal(OpVariable, tPtrPcStruct, vPc, StorageClassPushConstant);

        // UBO (set 0, binding 0): struct { uvec4 w[77]; uvec4 k; }
        uint tArr77 = b.NewId(); b.EmitType(OpTypeArray, tArr77, tUVec4, c77);
        b.EmitDecoration(OpDecorate, tArr77, DecorationArrayStride, 16u);
        uint tUboStruct = b.NewId(); b.EmitType(OpTypeStruct, tUboStruct, tArr77, tUVec4);
        b.EmitDecoration(OpDecorate, tUboStruct, DecorationBlock);
        b.EmitDecoration(OpMemberDecorate, tUboStruct, 0u, DecorationOffset, 0u);
        b.EmitDecoration(OpMemberDecorate, tUboStruct, 1u, DecorationOffset, 77u * 16u);
        uint tPtrUboStruct = b.NewId(); b.EmitType(OpTypePointer, tPtrUboStruct, StorageClassUniform, tUboStruct);
        uint tPtrUboUVec4 = b.NewId(); b.EmitType(OpTypePointer, tPtrUboUVec4, StorageClassUniform, tUVec4);
        uint vUbo = b.NewId(); b.EmitGlobal(OpVariable, tPtrUboStruct, vUbo, StorageClassUniform);
        b.EmitDecoration(OpDecorate, vUbo, DecorationDescriptorSet, 0u);
        b.EmitDecoration(OpDecorate, vUbo, DecorationBinding, 0u);

        // SSBO (set 0, binding 1): SPIR-V 1.3 StorageBuffer Block struct { uint data[]; }
        uint tRtArr = b.NewId(); b.EmitType(OpTypeRuntimeArray, tRtArr, tU32);
        b.EmitDecoration(OpDecorate, tRtArr, DecorationArrayStride, 4u);
        uint tSsboStruct = b.NewId(); b.EmitType(OpTypeStruct, tSsboStruct, tRtArr);
        b.EmitDecoration(OpDecorate, tSsboStruct, DecorationBlock);
        b.EmitDecoration(OpMemberDecorate, tSsboStruct, 0u, DecorationOffset, 0u);
        uint tPtrSsboStruct = b.NewId(); b.EmitType(OpTypePointer, tPtrSsboStruct, StorageClassStorageBuffer, tSsboStruct);
        uint tPtrSsboU32 = b.NewId(); b.EmitType(OpTypePointer, tPtrSsboU32, StorageClassStorageBuffer, tU32);
        uint vSsbo = b.NewId(); b.EmitGlobal(OpVariable, tPtrSsboStruct, vSsbo, StorageClassStorageBuffer);
        b.EmitDecoration(OpDecorate, vSsbo, DecorationDescriptorSet, 0u);
        b.EmitDecoration(OpDecorate, vSsbo, DecorationBinding, 1u);

        uint tPtrFnU32 = b.NewId(); b.EmitType(OpTypePointer, tPtrFnU32, StorageClassFunction, tU32);

        uint BinOp(uint op, uint ty, uint x, uint y)
        {
            uint r = b.NewId();
            b.EmitFunc(op, ty, r, x, y);
            return r;
        }
        uint IAdd(uint x, uint y) => BinOp(OpIAdd, tU32, x, y);
        uint IMul(uint x, uint y) => BinOp(OpIMul, tU32, x, y);
        uint BOr(uint x, uint y) => BinOp(OpBitwiseOr, tU32, x, y);
        uint BXor(uint x, uint y) => BinOp(OpBitwiseXor, tU32, x, y);
        uint BAnd(uint x, uint y) => BinOp(OpBitwiseAnd, tU32, x, y);
        uint Rotl(uint x, int shift) => shift == 0
            ? x
            : BOr(BinOp(OpShiftLeftLogical, tU32, x, Cu((uint)shift)),
                  BinOp(OpShiftRightLogical, tU32, x, Cu((uint)(32 - shift))));

        uint MixOp(int phase, uint rb, uint rc, uint rd) => phase switch
        {
            0 => BXor(rd, BAnd(rb, BXor(rc, rd))),
            2 => BXor(rb, BAnd(BXor(rb, rc), BXor(rb, rd))),
            _ => BXor(BXor(rb, rc), rd)
        };

        uint fnMain = b.NewId();
        uint lblEntry = b.NewId();
        uint lblHdr = b.NewId();
        uint lblBody = b.NewId();
        uint lblCont = b.NewId();
        uint lblExit = b.NewId();

        b.EmitFunc(OpFunction, tVoid, fnMain, 0u, tFn);
        b.EmitFunc(OpLabel, lblEntry);

        uint vOff = b.NewId(); b.EmitFunc(OpVariable, tPtrFnU32, vOff, StorageClassFunction);
        uint vIdx = b.NewId(); b.EmitFunc(OpVariable, tPtrFnU32, vIdx, StorageClassFunction);

        uint pGidX = b.NewId(); b.EmitFunc(OpAccessChain, tPtrInU32, pGidX, vGid, c0);
        uint gidX = b.NewId(); b.EmitFunc(OpLoad, tU32, gidX, pGidX);
        b.EmitFunc(OpStore, vOff, gidX);

        uint[] pc = new uint[9];
        for (uint i = 0; i < 9u; i++)
        {
            uint pp = b.NewId(); b.EmitFunc(OpAccessChain, tPtrPcU32, pp, vPc, Cu(i));
            uint pv = b.NewId(); b.EmitFunc(OpLoad, tU32, pv, pp);
            pc[i] = pv;
        }

        uint pFirst = pc[0], pCount = pc[1], pMaskLow = pc[2], pTargetLow = pc[3];
        uint pMaskHigh = pc[4], pTargetHigh = pc[5], pCap = pc[6], pStride = pc[7], pMode = pc[8];
        uint tLowNoK3 = IAdd(Rotl(IAdd(pTargetLow, Cu(unchecked(0u - 0xC3D2E1F0u))), 2), cNegK3);

        uint pKVec = b.NewId(); b.EmitFunc(OpAccessChain, tPtrUboUVec4, pKVec, vUbo, c1);
        uint kVec = b.NewId(); b.EmitFunc(OpLoad, tUVec4, kVec, pKVec);
        uint[] ks = new uint[4];
        for (uint comp = 0; comp < 4u; comp++)
        {
            uint kv = b.NewId();
            b.EmitFunc(OpCompositeExtract, tU32, kv, kVec, comp);
            ks[comp] = kv;
        }

        var wVecs = new uint[Sha1ScheduleHelper.ScheduleRounds][];
        for (int t = 5; t < Sha1ScheduleHelper.ScheduleRounds; t++)
        {
            if (t is 14 or 15) continue;
            uint pw = b.NewId(); b.EmitFunc(OpAccessChain, tPtrUboUVec4, pw, vUbo, c0, Cu((uint)t));
            uint wv = b.NewId(); b.EmitFunc(OpLoad, tUVec4, wv, pw);
            uint[] comps = new uint[4];
            for (uint comp = 0; comp < 4u; comp++)
            {
                uint ce = b.NewId();
                b.EmitFunc(OpCompositeExtract, tU32, ce, wv, comp);
                comps[comp] = ce;
            }
            wVecs[t] = comps;
        }

        b.EmitFunc(OpBranch, lblHdr);
        b.EmitFunc(OpLabel, lblHdr);
        uint curOff = b.NewId(); b.EmitFunc(OpLoad, tU32, curOff, vOff);
        uint cond = BinOp(OpULessThan, tBool, curOff, pCount);
        b.EmitFunc(OpLoopMerge, lblExit, lblCont, 0u);
        b.EmitFunc(OpBranchConditional, cond, lblBody, lblExit);

        b.EmitFunc(OpLabel, lblBody);
        uint ts = IAdd(pFirst, curOff);

        var twMap = new Dictionary<uint, uint> { [1u] = ts };
        foreach (int shift in xorPlan.RequiredRotations)
        {
            twMap[1u << shift] = Rotl(ts, shift);
        }

        foreach (TimestampXorStep step in xorPlan.Steps)
        {
            twMap[step.ResultMask] = BXor(twMap[step.LeftMask], twMap[step.RightMask]);
        }

        uint shA = Cu(0x67452301u);
        uint shB = Cu(0xEFCDAB89u);
        uint shC = Cu(0x98BADCFEu);
        uint shD = Cu(0x10325476u);
        uint shE = Cu(0xC3D2E1F0u);

        for (int t = 0; t < 5; t++)
        {
            uint w = t == 1 ? IAdd(ts, ks[0]) : Cu(templateFixed[t]);
            uint nx = IAdd(IAdd(shE, MixOp(t / 20, shB, shC, shD)), IAdd(Rotl(shA, 5), w));
            shE = shD;
            shD = shC;
            shC = Rotl(shB, 30);
            shB = shA;
            shA = nx;
        }

        uint[] la = { shA, shA, shA, shA };
        uint[] lb = { shB, shB, shB, shB };
        uint[] lc = { shC, shC, shC, shC };
        uint[] ld = { shD, shD, shD, shD };
        uint[] le = { shE, shE, shE, shE };

        // Round 5 shared base5 & c5
        uint base5 = IAdd(IAdd(shE, MixOp(0, shB, shC, shD)), Rotl(shA, 5));
        uint c5 = Rotl(shB, 30);
        for (int lane = 0; lane < 4; lane++)
        {
            le[lane] = shD;
            ld[lane] = shC;
            lc[lane] = c5;
            lb[lane] = shA;
            la[lane] = IAdd(base5, wVecs[5][lane]);
        }

        // Round 6 shared base6 & c6
        uint base6 = IAdd(le[0], MixOp(0, lb[0], lc[0], ld[0]));
        uint c6 = Rotl(lb[0], 30);
        for (int lane = 0; lane < 4; lane++)
        {
            uint nx = IAdd(IAdd(base6, Rotl(la[lane], 5)), wVecs[6][lane]);
            le[lane] = ld[lane];
            ld[lane] = lc[lane];
            lc[lane] = c6;
            lb[lane] = la[lane];
            la[lane] = nx;
        }

        for (int t = 7; t < Sha1ScheduleHelper.ScheduleRounds - 1; t++)
        {
            uint mask = masks[t];
            int phase = t / 20;
            uint kv = ks[phase];

            for (int lane = 0; lane < 4; lane++)
            {
                uint t1 = IAdd(IAdd(le[lane], MixOp(phase, lb[lane], lc[lane], ld[lane])), Rotl(la[lane], 5));
                uint bw = t is 14 or 15 ? Cu(templateFixed[t]) : wVecs[t][lane];

                if (t == 75)
                {
                    uint a76Nk = IAdd(t1, BXor(bw, twMap[masks[75]]));
                    uint isFullLow = BinOp(OpIEqual, tBool, pMaskLow, cAllOnes);
                    uint eqFast = BinOp(OpIEqual, tBool, a76Nk, tLowNoK3);
                    uint a76Full = IAdd(a76Nk, kv);
                    uint h4 = IAdd(Rotl(a76Full, 30), Cu(0xC3D2E1F0u));
                    uint eqMasked = BinOp(OpIEqual, tBool, BAnd(h4, pMaskLow), pTargetLow);
                    uint lowOk = b.NewId();
                    b.EmitFunc(OpSelect, tBool, lowOk, isFullLow, eqFast, eqMasked);

                    uint lHitChk = b.NewId();
                    uint lAfterLane = b.NewId();
                    b.EmitFunc(OpSelectionMerge, lAfterLane, 0u);
                    b.EmitFunc(OpBranchConditional, lowOk, lHitChk, lAfterLane);

                    b.EmitFunc(OpLabel, lHitChk);
                    uint isZeroHigh = BinOp(OpIEqual, tBool, pMaskHigh, c0);
                    uint c76 = Rotl(lb[lane], 30);
                    uint a77 = IAdd(
                        IAdd(IAdd(ld[lane], MixOp(3, la[lane], c76, lc[lane])), Rotl(a76Full, 5)),
                        IAdd(BXor(wVecs[76][lane], twMap[masks[76]]), kv));
                    uint h3 = IAdd(Rotl(a77, 30), Cu(0x10325476u));
                    uint highEq = BinOp(OpIEqual, tBool, BAnd(h3, pMaskHigh), pTargetHigh);
                    uint allOk = BinOp(OpLogicalOr, tBool, isZeroHigh, highEq);

                    uint lStore = b.NewId();
                    uint lEndChk = b.NewId();
                    b.EmitFunc(OpSelectionMerge, lEndChk, 0u);
                    b.EmitFunc(OpBranchConditional, allOk, lStore, lEndChk);

                    b.EmitFunc(OpLabel, lStore);
                    uint slot = IMul(Cu((uint)lane), IAdd(pCap, c1));
                    uint pCntPtr = b.NewId(); b.EmitFunc(OpAccessChain, tPtrSsboU32, pCntPtr, vSsbo, c0, slot);
                    uint isFindOne = BinOp(OpINotEqual, tBool, pMode, c0);
                    uint lCas = b.NewId();
                    uint lAdd = b.NewId();
                    uint lGotIdx = b.NewId();
                    b.EmitFunc(OpSelectionMerge, lGotIdx, 0u);
                    b.EmitFunc(OpBranchConditional, isFindOne, lCas, lAdd);

                    b.EmitFunc(OpLabel, lCas);
                    uint idxCas = b.NewId();
                    b.EmitFunc(OpAtomicCompareExchange, tU32, idxCas, pCntPtr, c1, c0, c0, c1, c0);
                    b.EmitFunc(OpStore, vIdx, idxCas);
                    b.EmitFunc(OpBranch, lGotIdx);

                    b.EmitFunc(OpLabel, lAdd);
                    uint idxAdd = b.NewId();
                    b.EmitFunc(OpAtomicIAdd, tU32, idxAdd, pCntPtr, c1, c0, c1);
                    b.EmitFunc(OpStore, vIdx, idxAdd);
                    b.EmitFunc(OpBranch, lGotIdx);

                    b.EmitFunc(OpLabel, lGotIdx);
                    uint gotIdx = b.NewId(); b.EmitFunc(OpLoad, tU32, gotIdx, vIdx);
                    uint inCap = BinOp(OpULessThan, tBool, gotIdx, pCap);
                    uint lWriteTs = b.NewId();
                    uint lDoneStore = b.NewId();
                    b.EmitFunc(OpSelectionMerge, lDoneStore, 0u);
                    b.EmitFunc(OpBranchConditional, inCap, lWriteTs, lDoneStore);

                    b.EmitFunc(OpLabel, lWriteTs);
                    uint outPos = IAdd(slot, IAdd(c1, gotIdx));
                    uint pOutPtr = b.NewId(); b.EmitFunc(OpAccessChain, tPtrSsboU32, pOutPtr, vSsbo, c0, outPos);
                    b.EmitFunc(OpStore, pOutPtr, ts);
                    b.EmitFunc(OpBranch, lDoneStore);

                    b.EmitFunc(OpLabel, lDoneStore);
                    b.EmitFunc(OpBranch, lEndChk);

                    b.EmitFunc(OpLabel, lEndChk);
                    b.EmitFunc(OpBranch, lAfterLane);

                    b.EmitFunc(OpLabel, lAfterLane);
                    continue;
                }

                uint nx = mask == 0u
                    ? IAdd(t1, bw)
                    : IAdd(t1, IAdd(BXor(bw, twMap[mask]), kv));

                le[lane] = ld[lane];
                ld[lane] = lc[lane];
                lc[lane] = Rotl(lb[lane], 30);
                lb[lane] = la[lane];
                la[lane] = nx;
            }
        }

        b.EmitFunc(OpBranch, lblCont);
        b.EmitFunc(OpLabel, lblCont);
        b.EmitFunc(OpStore, vOff, IAdd(curOff, pStride));
        b.EmitFunc(OpBranch, lblHdr);

        b.EmitFunc(OpLabel, lblExit);
        b.EmitFunc(OpReturn);
        b.EmitFunc(OpFunctionEnd);

        // SPIR-V 1.3: OpEntryPoint MUST list only Input/Output globals (vGid).
        return b.Assemble(fnMain, workgroupSize, vGid);
    }

    private sealed class Builder
    {
        private readonly List<uint> _decorations = new();
        private readonly List<uint> _typesConsts = new();
        private readonly List<uint> _globals = new();
        private readonly List<uint> _func = new();
        private readonly Dictionary<uint, uint> _constU32Map = new();
        private uint _nextId = 1;

        public uint NewId() => _nextId++;

        public uint ConstU32(uint typeU32, uint value)
        {
            if (!_constU32Map.TryGetValue(value, out uint id))
            {
                id = NewId();
                Emit(_typesConsts, OpConstant, typeU32, id, value);
                _constU32Map[value] = id;
            }
            return id;
        }

        public void EmitDecoration(uint opcode, params uint[] operands) => Emit(_decorations, opcode, operands);
        public void EmitType(uint opcode, params uint[] operands) => Emit(_typesConsts, opcode, operands);
        public void EmitGlobal(uint opcode, params uint[] operands) => Emit(_globals, opcode, operands);
        public void EmitFunc(uint opcode, params uint[] operands) => Emit(_func, opcode, operands);

        public byte[] Assemble(uint entryFnId, int workgroupSize, uint vGid)
        {
            var words = new List<uint>(24_000)
            {
                SpirvMagic,
                SpirvVersion13,
                0u,
                _nextId,
                0u
            };

            Emit(words, OpCapability, CapabilityShader);
            Emit(words, OpMemoryModel, 0u, 1u); // Logical, GLSL450

            byte[] entryBytes = Encoding.ASCII.GetBytes("main\0");
            int paddedWords = (entryBytes.Length + 3) / 4;
            var epWords = new uint[2 + paddedWords + 1];
            epWords[0] = ExecutionModelGlCompute;
            epWords[1] = entryFnId;
            for (int w = 0; w < paddedWords; w++)
            {
                uint word = 0u;
                for (int b = 0; b < 4; b++)
                {
                    int idx = w * 4 + b;
                    if (idx < entryBytes.Length)
                    {
                        word |= (uint)entryBytes[idx] << (8 * b);
                    }
                }
                epWords[2 + w] = word;
            }
            epWords[2 + paddedWords] = vGid;
            Emit(words, OpEntryPoint, epWords);

            Emit(words, OpExecutionMode, entryFnId, ExecutionModeLocalSize, (uint)workgroupSize, 1u, 1u);

            words.AddRange(_decorations);
            words.AddRange(_typesConsts);
            words.AddRange(_globals);
            words.AddRange(_func);

            var bytes = new byte[words.Count * sizeof(uint)];
            Buffer.BlockCopy(words.ToArray(), 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static void Emit(List<uint> dest, uint opcode, params uint[] operands)
        {
            dest.Add((((uint)operands.Length + 1u) << 16) | opcode);
            dest.AddRange(operands);
        }
    }
}
