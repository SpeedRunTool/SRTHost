#include "CrashHandler.h"

#include <DbgHelp.h>
#include <TlHelp32.h>
#include <psapi.h>

#include "OverlayProtocol.h"

// Everything below the Install() boundary runs on a thread whose heap and stack may already be corrupt.
// That rules out the CRT, std::string, and any lock. All formatting is hand-rolled into static buffers,
// and every risky call is wrapped in SEH. The minidump is written FIRST, because it is the artifact we
// cannot regenerate; the text report follows. Functions containing __try hold no object with a
// destructor (C2712), which is another reason everything here is raw buffers.
//
// Adapted from SpeedrunTooling/SRTPluginProviderRE9's CrashHandler.cpp; trimmed to the shim (no game
// state, no logger ring), and it keeps the hook inventory, which is the shim-specific thing worth having.

namespace srtoverlay::core::CrashHandler
{
    namespace
    {
        constexpr size_t ReportBufferSize = 512 * 1024;
        constexpr size_t MaxModules = 512;
        constexpr size_t MaxStackFrames = 64;
        constexpr size_t MaxExtraMemoryRanges = 128;

        // A window of code either side of RIP and of every return address, so the crash path can be
        // disassembled without capturing whole modules (which produced multi-hundred-MB dumps in the
        // reference's field reports).
        constexpr ULONG CodeWindowBytes = 1024 * 1024;
        constexpr ULONG DataWindowBytes = 0x800;
        constexpr ULONG StackGuaranteeBytes = 64 * 1024;
        constexpr DWORD CrashWorkTimeoutMs = 60 * 1000;
        constexpr ULONG32 CommentStreamAType = 10;

        struct HookRecord
        {
            char Name[32];
            uintptr_t Target;
            bool Installed;
        };

        BuildIdentity g_selfIdentity{};
        BuildIdentity g_exeIdentity{};
        std::atomic_flag g_inHandler = ATOMIC_FLAG_INIT;

        char g_reportBuffer[ReportBufferSize]{};

        HookRecord g_hooks[MaxHookRecords]{};
        std::atomic<uint32_t> g_hookCount{0};

        wchar_t g_outputDir[MAX_PATH]{};
        wchar_t g_logPath[MAX_PATH]{};
        bool g_haveLogPath = false;
        wchar_t g_shimLeaf[MAX_PATH]{};
        wchar_t g_exeLeaf[MAX_PATH]{};

        struct ModuleEntry
        {
            uintptr_t Base;
            uint32_t Size;
            uint32_t LinkTimestamp;
            GUID PdbGuid;
            uint32_t PdbAge;
            bool HasPdbId;
            wchar_t Path[MAX_PATH];
        };

        ModuleEntry g_moduleSnapshot[MaxModules]{};
        uint32_t g_moduleSnapshotCount = 0;

        struct CrashJob
        {
            EXCEPTION_POINTERS* ExceptionInfo;
            DWORD FaultingThreadId;
            wchar_t DumpPath[MAX_PATH];
            wchar_t ReportPath[MAX_PATH];
            bool DumpWritten;
            DWORD DumpError;
        };

        CrashJob g_job{};

        HANDLE g_workerThread = nullptr;
        HANDLE g_workerStart = nullptr;
        HANDLE g_workerDone = nullptr;
        std::atomic<bool> g_workerShutdown{false};
        HANDLE g_reportFile = INVALID_HANDLE_VALUE;

        // The filter that was in place when we installed ours, restored on Uninstall so an unloaded
        // shim does not leave the game calling into freed module memory.
        LPTOP_LEVEL_EXCEPTION_FILTER g_previousFilter = nullptr;

        struct MemoryRange
        {
            ULONG64 Base;
            ULONG Size;
        };

        struct ExtraMemory
        {
            MemoryRange Ranges[MaxExtraMemoryRanges];
            ULONG Count;
            ULONG Next;
        };

        ExtraMemory g_extraMemory{};

        uintptr_t g_faultFrames[MaxStackFrames]{};
        size_t g_faultFrameCount = 0;

        // ---- Allocation-free text building ----

        struct Appender
        {
            char* Cur;
            char* End;

            void Ch(char c) noexcept { if (Cur < End) *Cur++ = c; }
            void Str(const char* s) noexcept
            {
                if (s == nullptr) return;
                while (*s != '\0' && Cur < End) *Cur++ = *s++;
            }
            void Wide(const wchar_t* s) noexcept
            {
                if (s == nullptr) return;
                while (*s != L'\0' && Cur < End)
                {
                    const wchar_t wc = *s++;
                    *Cur++ = (wc < 0x80) ? static_cast<char>(wc) : '?';
                }
            }
            void Hex(uint64_t value, int digits) noexcept
            {
                static constexpr char kDigits[] = "0123456789ABCDEF";
                if (digits <= 0)
                {
                    digits = 1;
                    for (uint64_t probe = value; probe >= 16; probe >>= 4) ++digits;
                }
                for (int shift = (digits - 1) * 4; shift >= 0; shift -= 4)
                    Ch(kDigits[(value >> shift) & 0xF]);
            }
            void Dec(uint64_t value) noexcept
            {
                char tmp[24];
                int i = 0;
                do { tmp[i++] = static_cast<char>('0' + (value % 10)); value /= 10; }
                while (value != 0 && i < static_cast<int>(sizeof(tmp)));
                while (i > 0) Ch(tmp[--i]);
            }
            void Dec2(unsigned value) noexcept
            {
                Ch(static_cast<char>('0' + ((value / 10) % 10)));
                Ch(static_cast<char>('0' + (value % 10)));
            }
            [[nodiscard]] size_t Length(const char* start) const noexcept { return static_cast<size_t>(Cur - start); }
        };

        void AppendGuid(Appender& out, const GUID& guid) noexcept
        {
            out.Hex(guid.Data1, 8);
            out.Hex(guid.Data2, 4);
            out.Hex(guid.Data3, 4);
            for (const unsigned char byte : guid.Data4) out.Hex(byte, 2);
        }

        void AppendGuidBraced(Appender& out, const GUID& guid) noexcept
        {
            out.Ch('{');
            out.Hex(guid.Data1, 8); out.Ch('-');
            out.Hex(guid.Data2, 4); out.Ch('-');
            out.Hex(guid.Data3, 4); out.Ch('-');
            out.Hex(guid.Data4[0], 2); out.Hex(guid.Data4[1], 2); out.Ch('-');
            for (size_t i = 2; i < 8; ++i) out.Hex(guid.Data4[i], 2);
            out.Ch('}');
        }

        void AppendPdbIdentity(Appender& out, const GUID& guid, uint32_t age) noexcept
        {
            out.Str("PDB GUID ");
            AppendGuidBraced(out, guid);
            out.Str(" Age ");
            out.Dec(age);
            out.Str(" (symbol key ");
            AppendGuid(out, guid);
            out.Hex(age, 0);
            out.Ch(')');
        }

        // ---- Module / memory helpers ----

        bool IsReadable(uintptr_t address, size_t size) noexcept
        {
            MEMORY_BASIC_INFORMATION mbi{};
            if (VirtualQuery(reinterpret_cast<LPCVOID>(address), &mbi, sizeof(mbi)) == 0)
                return false;
            if (mbi.State != MEM_COMMIT || (mbi.Protect & PAGE_GUARD) != 0)
                return false;
            switch (mbi.Protect & 0xFF)
            {
                case PAGE_READONLY:
                case PAGE_READWRITE:
                case PAGE_WRITECOPY:
                case PAGE_EXECUTE_READ:
                case PAGE_EXECUTE_READWRITE:
                case PAGE_EXECUTE_WRITECOPY:
                    break;
                default:
                    return false;
            }
            const auto regionEnd = reinterpret_cast<uintptr_t>(mbi.BaseAddress) + mbi.RegionSize;
            return address + size <= regionEnd;
        }

        const wchar_t* LeafName(const wchar_t* path) noexcept
        {
            const wchar_t* leaf = path;
            for (const wchar_t* scan = path; *scan != L'\0'; ++scan)
                if (*scan == L'\\' || *scan == L'/') leaf = scan + 1;
            return leaf;
        }

        void CopyWide(wchar_t* dst, size_t dstCount, const wchar_t* src) noexcept
        {
            size_t i = 0;
            if (src != nullptr)
                for (; i + 1 < dstCount && src[i] != L'\0'; ++i) dst[i] = src[i];
            dst[i] = L'\0';
        }

        bool EqualsIgnoreCaseW(const wchar_t* a, const wchar_t* b) noexcept
        {
            while (*a != L'\0' && *b != L'\0')
            {
                const wchar_t la = (*a >= L'A' && *a <= L'Z') ? static_cast<wchar_t>(*a + 32) : *a;
                const wchar_t lb = (*b >= L'A' && *b <= L'Z') ? static_cast<wchar_t>(*b + 32) : *b;
                if (la != lb) return false;
                ++a; ++b;
            }
            return *a == L'\0' && *b == L'\0';
        }

        bool ContainsIgnoreCaseW(const wchar_t* haystack, const wchar_t* needle) noexcept
        {
            for (const wchar_t* start = haystack; *start != L'\0'; ++start)
            {
                const wchar_t* a = start;
                const wchar_t* b = needle;
                while (*a != L'\0' && *b != L'\0')
                {
                    const wchar_t la = (*a >= L'A' && *a <= L'Z') ? static_cast<wchar_t>(*a + 32) : *a;
                    if (la != *b) break;
                    ++a; ++b;
                }
                if (*b == L'\0') return true;
            }
            return false;
        }

        BuildIdentity ReadBuildIdentity(const uint8_t* base) noexcept;

        void TakeModuleSnapshot() noexcept
        {
            g_moduleSnapshotCount = 0;

            HMODULE modules[MaxModules];
            DWORD bytesNeeded = 0;
            if (EnumProcessModules(GetCurrentProcess(), modules, sizeof(modules), &bytesNeeded) == FALSE)
                return;

            const size_t reported = bytesNeeded / sizeof(HMODULE);
            const size_t count = (reported < MaxModules) ? reported : MaxModules;

            for (size_t i = 0; i < count; ++i)
            {
                const BuildIdentity identity = ReadBuildIdentity(reinterpret_cast<const uint8_t*>(modules[i]));
                ModuleEntry& entry = g_moduleSnapshot[g_moduleSnapshotCount];
                entry.Base = reinterpret_cast<uintptr_t>(modules[i]);
                entry.Size = identity.SizeOfImage;
                entry.LinkTimestamp = identity.LinkTimestamp;
                entry.PdbGuid = identity.PdbGuid;
                entry.PdbAge = identity.PdbAge;
                entry.HasPdbId = identity.Valid;
                entry.Path[0] = L'\0';
                GetModuleFileNameW(modules[i], entry.Path, MAX_PATH);
                ++g_moduleSnapshotCount;
            }
        }

        const ModuleEntry* FindModule(uintptr_t address) noexcept
        {
            for (uint32_t i = 0; i < g_moduleSnapshotCount; ++i)
            {
                const ModuleEntry& entry = g_moduleSnapshot[i];
                if (entry.Size != 0 && address >= entry.Base && address < entry.Base + entry.Size)
                    return &entry;
            }
            return nullptr;
        }

        void AppendResolvedAddress(Appender& out, uintptr_t address) noexcept
        {
            out.Str("0x");
            out.Hex(address, 16);

            const ModuleEntry* owner = FindModule(address);
            if (owner == nullptr)
            {
                out.Str(g_moduleSnapshotCount == 0 ? "  <no module snapshot>" : "  <not in any loaded module>");
                return;
            }

            out.Str("  ");
            out.Wide(LeafName(owner->Path));
            out.Str("+0x");
            out.Hex(address - owner->Base, 0);
        }

        BuildIdentity ReadBuildIdentity(const uint8_t* base) noexcept
        {
            BuildIdentity identity{};
            __try
            {
                const auto* dosHeader = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
                if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE) return identity;

                const auto* ntHeaders = reinterpret_cast<const IMAGE_NT_HEADERS*>(base + dosHeader->e_lfanew);
                if (ntHeaders->Signature != IMAGE_NT_SIGNATURE) return identity;

                identity.ImageBase = reinterpret_cast<uintptr_t>(base);
                identity.SizeOfImage = ntHeaders->OptionalHeader.SizeOfImage;
                identity.LinkTimestamp = ntHeaders->FileHeader.TimeDateStamp;

                const auto& debugDir = ntHeaders->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DEBUG];
                if (debugDir.VirtualAddress == 0 || debugDir.Size < sizeof(IMAGE_DEBUG_DIRECTORY))
                    return identity;

                const auto* entries = reinterpret_cast<const IMAGE_DEBUG_DIRECTORY*>(base + debugDir.VirtualAddress);
                const size_t entryCount = debugDir.Size / sizeof(IMAGE_DEBUG_DIRECTORY);

                for (size_t i = 0; i < entryCount; ++i)
                {
                    if (entries[i].Type != IMAGE_DEBUG_TYPE_CODEVIEW || entries[i].AddressOfRawData == 0)
                        continue;

                    const uint8_t* record = base + entries[i].AddressOfRawData;
                    if (*reinterpret_cast<const uint32_t*>(record) != 0x53445352u) // 'SDSR'
                        continue;

                    identity.PdbGuid = *reinterpret_cast<const GUID*>(record + 4);
                    identity.PdbAge = *reinterpret_cast<const uint32_t*>(record + 20);

                    const auto* path = reinterpret_cast<const char*>(record + 24);
                    size_t n = 0;
                    while (n + 1 < sizeof(identity.PdbPath) && path[n] != '\0')
                    {
                        identity.PdbPath[n] = path[n];
                        ++n;
                    }
                    identity.PdbPath[n] = '\0';
                    identity.Valid = true;
                    break;
                }
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                identity.Valid = false;
            }
            return identity;
        }

        void AppendBuildIdentity(Appender& out, const char* label, const BuildIdentity& identity) noexcept
        {
            out.Str(label);
            if (!identity.Valid)
            {
                out.Str(" : <no CodeView record>\n");
                return;
            }
            out.Str("\n    PDB identity  : ");
            AppendPdbIdentity(out, identity.PdbGuid, identity.PdbAge);
            out.Str("\n    Link timestamp: 0x");
            out.Hex(identity.LinkTimestamp, 8);
            out.Str("\n    Image base    : 0x");
            out.Hex(identity.ImageBase, 16);
            out.Str("  size 0x");
            out.Hex(identity.SizeOfImage, 0);
            out.Str("\n    PDB path      : ");
            out.Str(identity.PdbPath);
            out.Ch('\n');
        }

        // ---- Stack unwinding (no SymFromAddr; module+RVA is enough with the matching PDB) ----

        size_t UnwindStack(const CONTEXT& start, uintptr_t* frames, size_t maxFrames) noexcept
        {
            size_t count = 0;
            __try
            {
#if defined(_M_X64)
                // Table-driven unwinding: x64 has no reliable frame-pointer chain.
                CONTEXT ctx = start;
                while (count < maxFrames && ctx.Rip != 0)
                {
                    frames[count++] = static_cast<uintptr_t>(ctx.Rip);

                    DWORD64 imageBase = 0;
                    PRUNTIME_FUNCTION function = RtlLookupFunctionEntry(ctx.Rip, &imageBase, nullptr);
                    if (function == nullptr)
                    {
                        if (!IsReadable(static_cast<uintptr_t>(ctx.Rsp), sizeof(DWORD64)))
                            break;
                        ctx.Rip = *reinterpret_cast<DWORD64*>(ctx.Rsp);
                        ctx.Rsp += sizeof(DWORD64);
                        continue;
                    }

                    PVOID handlerData = nullptr;
                    DWORD64 establisherFrame = 0;
                    const DWORD64 previousRsp = ctx.Rsp;
                    RtlVirtualUnwind(UNW_FLAG_NHANDLER, imageBase, ctx.Rip, function, &ctx, &handlerData, &establisherFrame, nullptr);
                    if (ctx.Rsp <= previousRsp)
                        break;
                }
#else
                // x86 has no unwind tables in the x64 sense; walk the EBP frame chain. Best-effort:
                // FPO-optimised frames break the chain, but the full per-thread stack is in the .dmp.
                uintptr_t ip = start.Eip;
                uintptr_t bp = start.Ebp;
                while (count < maxFrames && ip != 0)
                {
                    frames[count++] = ip;
                    if (!IsReadable(bp, sizeof(uintptr_t) * 2))
                        break;
                    const uintptr_t nextBp = *reinterpret_cast<uintptr_t*>(bp);
                    const uintptr_t retAddr = *reinterpret_cast<uintptr_t*>(bp + sizeof(uintptr_t));
                    if (nextBp <= bp) // no progress / not a real frame chain
                        break;
                    ip = retAddr;
                    bp = nextBp;
                }
#endif
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
            }
            return count;
        }

        // ---- Minidump callbacks ----

        bool IsWhitelistedModule(const wchar_t* fullPath) noexcept
        {
            if (fullPath == nullptr) return false;
            const wchar_t* leaf = LeafName(fullPath);

            if ((g_exeLeaf[0] != L'\0' && EqualsIgnoreCaseW(leaf, g_exeLeaf)) ||
                (g_shimLeaf[0] != L'\0' && EqualsIgnoreCaseW(leaf, g_shimLeaf)) ||
                EqualsIgnoreCaseW(leaf, L"dinput8.dll"))
                return true;

            return ContainsIgnoreCaseW(fullPath, L"reframework") || ContainsIgnoreCaseW(fullPath, L"srtoverlay");
        }

        void AddExtraRange(uintptr_t center, ULONG halfWindow) noexcept
        {
            if (center == 0) return;

            MEMORY_BASIC_INFORMATION mbi{};
            if (VirtualQuery(reinterpret_cast<LPCVOID>(center), &mbi, sizeof(mbi)) == 0 || mbi.State != MEM_COMMIT)
                return;

            const auto regionStart = reinterpret_cast<uintptr_t>(mbi.BaseAddress);
            const uintptr_t regionEnd = regionStart + mbi.RegionSize;

            uintptr_t begin = (center > halfWindow) ? center - halfWindow : 0;
            uintptr_t end = center + halfWindow;
            if (begin < regionStart) begin = regionStart;
            if (end > regionEnd) end = regionEnd;
            if (end <= begin) return;

            for (ULONG i = 0; i < g_extraMemory.Count; ++i)
            {
                MemoryRange& existing = g_extraMemory.Ranges[i];
                const uintptr_t existingBegin = static_cast<uintptr_t>(existing.Base);
                const uintptr_t existingEnd = existingBegin + existing.Size;
                if (begin > existingEnd || end < existingBegin) continue;

                const uintptr_t mergedBegin = (begin < existingBegin) ? begin : existingBegin;
                const uintptr_t mergedEnd = (end > existingEnd) ? end : existingEnd;
                existing.Base = mergedBegin;
                existing.Size = static_cast<ULONG>(mergedEnd - mergedBegin);
                return;
            }

            if (g_extraMemory.Count >= MaxExtraMemoryRanges) return;
            g_extraMemory.Ranges[g_extraMemory.Count].Base = begin;
            g_extraMemory.Ranges[g_extraMemory.Count].Size = static_cast<ULONG>(end - begin);
            ++g_extraMemory.Count;
        }

        void BuildExtraMemoryRanges(const CONTEXT* context) noexcept
        {
            g_extraMemory.Count = 0;
            g_extraMemory.Next = 0;
            if (context == nullptr) return;

#if defined(_M_X64)
            AddExtraRange(static_cast<uintptr_t>(context->Rip), CodeWindowBytes);
#else
            AddExtraRange(static_cast<uintptr_t>(context->Eip), CodeWindowBytes);
#endif
            for (size_t i = 0; i < g_faultFrameCount; ++i)
                AddExtraRange(g_faultFrames[i], CodeWindowBytes);

#if defined(_M_X64)
            const DWORD64 registers[] = {
                context->Rax, context->Rcx, context->Rdx, context->Rbx,
                context->Rsp, context->Rbp, context->Rsi, context->Rdi,
                context->R8, context->R9, context->R10, context->R11,
                context->R12, context->R13, context->R14, context->R15};
#else
            const DWORD registers[] = {
                context->Eax, context->Ecx, context->Edx, context->Ebx,
                context->Esp, context->Ebp, context->Esi, context->Edi};
#endif
            for (const auto value : registers)
                AddExtraRange(static_cast<uintptr_t>(value), DataWindowBytes);

            if (g_selfIdentity.ImageBase != 0 && g_selfIdentity.SizeOfImage != 0 &&
                g_extraMemory.Count < MaxExtraMemoryRanges)
            {
                g_extraMemory.Ranges[g_extraMemory.Count].Base = g_selfIdentity.ImageBase;
                g_extraMemory.Ranges[g_extraMemory.Count].Size = g_selfIdentity.SizeOfImage;
                ++g_extraMemory.Count;
            }
        }

        BOOL CALLBACK MiniDumpCallback(PVOID, const PMINIDUMP_CALLBACK_INPUT callbackInput,
                                       PMINIDUMP_CALLBACK_OUTPUT callbackOutput)
        {
            if (callbackInput == nullptr || callbackOutput == nullptr) return FALSE;

            switch (callbackInput->CallbackType)
            {
                case ModuleCallback:
                    if (!IsWhitelistedModule(callbackInput->Module.FullPath))
                        callbackOutput->ModuleWriteFlags &= ~static_cast<ULONG>(ModuleWriteDataSeg | ModuleWriteCodeSegs);
                    return TRUE;

                case MemoryCallback:
                    if (g_extraMemory.Next < g_extraMemory.Count)
                    {
                        const MemoryRange& range = g_extraMemory.Ranges[g_extraMemory.Next++];
                        callbackOutput->MemoryBase = range.Base;
                        callbackOutput->MemorySize = range.Size;
                    }
                    else
                    {
                        callbackOutput->MemoryBase = 0;
                        callbackOutput->MemorySize = 0;
                    }
                    return TRUE;

                default:
                    return TRUE;
            }
        }

        // ---- Filenames and raw file IO ----

        void AppendTwoDigitsW(wchar_t*& cursor, unsigned value) noexcept
        {
            *cursor++ = static_cast<wchar_t>(L'0' + ((value / 10) % 10));
            *cursor++ = static_cast<wchar_t>(L'0' + (value % 10));
        }

        // "<outputDir>\SRTOverlay_YYYYMMDD-HHMMSS.mmmUTC<suffix>", no allocation.
        void BuildCrashFileName(const SYSTEMTIME& now, const wchar_t* suffix, wchar_t (&out)[MAX_PATH]) noexcept
        {
            wchar_t* cursor = out;
            for (const wchar_t* p = g_outputDir; *p != L'\0'; ++p) *cursor++ = *p;
            if (cursor != out && *(cursor - 1) != L'\\' && *(cursor - 1) != L'/') *cursor++ = L'\\';

            static constexpr wchar_t kPrefix[] = L"SRTOverlay_";
            for (const wchar_t* p = kPrefix; *p != L'\0'; ++p) *cursor++ = *p;

            AppendTwoDigitsW(cursor, now.wYear / 100u);
            AppendTwoDigitsW(cursor, now.wYear % 100u);
            AppendTwoDigitsW(cursor, now.wMonth);
            AppendTwoDigitsW(cursor, now.wDay);
            *cursor++ = L'-';
            AppendTwoDigitsW(cursor, now.wHour);
            AppendTwoDigitsW(cursor, now.wMinute);
            AppendTwoDigitsW(cursor, now.wSecond);
            *cursor++ = L'.';
            *cursor++ = static_cast<wchar_t>(L'0' + ((now.wMilliseconds / 100u) % 10u));
            AppendTwoDigitsW(cursor, now.wMilliseconds % 100u);

            static constexpr wchar_t kUtc[] = L"UTC";
            for (const wchar_t* p = kUtc; *p != L'\0'; ++p) *cursor++ = *p;
            for (const wchar_t* p = suffix; *p != L'\0'; ++p) *cursor++ = *p;
            *cursor = L'\0';
        }

        void AppendToLogFile(const char* text, DWORD size) noexcept
        {
            if (!g_haveLogPath) return;

            const HANDLE file = CreateFileW(g_logPath, FILE_APPEND_DATA,
                                            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                            nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file == INVALID_HANDLE_VALUE) return;
            SetFilePointer(file, 0, nullptr, FILE_END);
            DWORD written = 0;
            WriteFile(file, text, size, &written, nullptr);
            CloseHandle(file);
        }

        // ---- Report sections ----

        void AppendExceptionSummary(Appender& out, const EXCEPTION_RECORD* record) noexcept
        {
            out.Str("Exception code   : 0x");
            out.Hex(record->ExceptionCode, 8);
            switch (record->ExceptionCode)
            {
                case EXCEPTION_ACCESS_VIOLATION:   out.Str("  (ACCESS_VIOLATION)"); break;
                case EXCEPTION_STACK_OVERFLOW:     out.Str("  (STACK_OVERFLOW)"); break;
                case EXCEPTION_ILLEGAL_INSTRUCTION:out.Str("  (ILLEGAL_INSTRUCTION)"); break;
                case EXCEPTION_INT_DIVIDE_BY_ZERO: out.Str("  (INT_DIVIDE_BY_ZERO)"); break;
                case EXCEPTION_PRIV_INSTRUCTION:   out.Str("  (PRIV_INSTRUCTION)"); break;
                case EXCEPTION_IN_PAGE_ERROR:      out.Str("  (IN_PAGE_ERROR)"); break;
                default: break;
            }

            out.Str("\nException address: 0x");
            out.Hex(reinterpret_cast<uintptr_t>(record->ExceptionAddress), 16);
            out.Ch('\n');

            if (record->ExceptionCode == EXCEPTION_ACCESS_VIOLATION && record->NumberParameters >= 2)
            {
                out.Str("Access violation : ");
                switch (record->ExceptionInformation[0])
                {
                    case 0: out.Str("READ from 0x"); break;
                    case 1: out.Str("WRITE to 0x"); break;
                    case 8: out.Str("EXECUTE at 0x"); break;
                    default: out.Str("UNKNOWN at 0x"); break;
                }
                out.Hex(record->ExceptionInformation[1], 16);
                out.Ch('\n');
            }

            out.Str("Parameters       :");
            for (DWORD i = 0; i < record->NumberParameters && i < EXCEPTION_MAXIMUM_PARAMETERS; ++i)
            {
                out.Str(" 0x");
                out.Hex(record->ExceptionInformation[i], 0);
            }
            out.Ch('\n');
        }

        void AppendRegisters(Appender& out, const CONTEXT* context) noexcept
        {
            struct NamedRegister { const char* Name; DWORD64 Value; };
#if defined(_M_X64)
            const NamedRegister registers[] = {
                {"RAX", context->Rax}, {"RBX", context->Rbx}, {"RCX", context->Rcx}, {"RDX", context->Rdx},
                {"RSI", context->Rsi}, {"RDI", context->Rdi}, {"RBP", context->Rbp}, {"RSP", context->Rsp},
                {"R8 ", context->R8}, {"R9 ", context->R9}, {"R10", context->R10}, {"R11", context->R11},
                {"R12", context->R12}, {"R13", context->R13}, {"R14", context->R14}, {"R15", context->R15},
                {"RIP", context->Rip}};
            constexpr int kHexDigits = 16;
#else
            const NamedRegister registers[] = {
                {"EAX", context->Eax}, {"EBX", context->Ebx}, {"ECX", context->Ecx}, {"EDX", context->Edx},
                {"ESI", context->Esi}, {"EDI", context->Edi}, {"EBP", context->Ebp}, {"ESP", context->Esp},
                {"EIP", context->Eip}};
            constexpr int kHexDigits = 8;
#endif

            out.Str("\n--- Registers ---\n");
            for (size_t i = 0; i < sizeof(registers) / sizeof(registers[0]); ++i)
            {
                out.Str(registers[i].Name);
                out.Str(" = 0x");
                out.Hex(registers[i].Value, kHexDigits);
                out.Str((i % 2 == 1) ? "\n" : "   ");
            }
            out.Str("\nEFlags = 0x");
            out.Hex(context->EFlags, 8);
            out.Ch('\n');
        }

        void AppendFaultingStack(Appender& out, DWORD faultingThreadId) noexcept
        {
            out.Str("\n--- Faulting thread stack (TID ");
            out.Dec(faultingThreadId);
            out.Str(") ---\n");

            if (g_faultFrameCount == 0)
            {
                out.Str("  <unwind produced no frames>\n");
                return;
            }

            for (size_t i = 0; i < g_faultFrameCount; ++i)
            {
                out.Str("  [");
                out.Dec(i);
                out.Str("] ");
                AppendResolvedAddress(out, g_faultFrames[i]);
                out.Ch('\n');
            }
        }

        void AppendShimState(Appender& out) noexcept
        {
            out.Str("\n--- SRT overlay shim state ---\n");
            out.Str("Protocol       : v");
            out.Dec(SRT_OVERLAY_PROTOCOL_VERSION);
            out.Ch('\n');

            AppendBuildIdentity(out, "SRTOverlay shim", g_selfIdentity);
            AppendBuildIdentity(out, "Host executable", g_exeIdentity);

            const uint32_t hookCount = g_hookCount.load(std::memory_order_acquire);
            out.Str("\nHooks recorded : ");
            out.Dec(hookCount);
            out.Str("\n\nHook targets. The owning module matters - another injector may have detoured\n"
                    "first, in which case we hooked their trampoline rather than the real function.\n"
                    "FAILED means the vtable write did not take and the detour is not live.\n");

            for (uint32_t i = 0; i < hookCount && i < MaxHookRecords; ++i)
            {
                out.Str(g_hooks[i].Installed ? "  [ ok  ] " : "  [FAILED] ");
                out.Str(g_hooks[i].Name);
                out.Str(" -> ");
                AppendResolvedAddress(out, g_hooks[i].Target);
                out.Ch('\n');
            }
            if (hookCount == 0)
                out.Str("  <none attached>\n");
        }

        void AppendModuleList(Appender& out) noexcept
        {
            out.Str("\n--- Loaded modules (pdbkey = 32-hex GUID followed by age in hex) ---\n");
            if (g_moduleSnapshotCount == 0)
            {
                out.Str("  <module snapshot unavailable>\n");
                return;
            }

            for (uint32_t i = 0; i < g_moduleSnapshotCount; ++i)
            {
                const ModuleEntry& entry = g_moduleSnapshot[i];
                out.Str("  0x");
                out.Hex(entry.Base, 16);
                out.Str(" 0x");
                out.Hex(entry.Size, 8);
                out.Str(" ts=0x");
                out.Hex(entry.LinkTimestamp, 8);
                out.Str(" pdbkey=");
                if (entry.HasPdbId)
                {
                    AppendGuid(out, entry.PdbGuid);
                    out.Hex(entry.PdbAge, 0);
                }
                else
                {
                    out.Str("<none>");
                }
                if (entry.Path[0] != L'\0')
                {
                    out.Ch(' ');
                    out.Wide(entry.Path);
                }
                out.Ch('\n');
            }
        }

        void AppendThreadInventory(Appender& out, DWORD faultingThreadId) noexcept
        {
            out.Str("\n--- Threads (per-thread stacks are in the .dmp) ---\n");

            const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
            if (snapshot == INVALID_HANDLE_VALUE)
            {
                out.Str("  <snapshot failed>\n");
                return;
            }

            const DWORD ownProcessId = GetCurrentProcessId();
            THREADENTRY32 entry{};
            entry.dwSize = sizeof(entry);
            if (Thread32First(snapshot, &entry) != FALSE)
            {
                do
                {
                    if (entry.th32OwnerProcessID != ownProcessId) continue;

                    out.Str("  TID ");
                    out.Dec(entry.th32ThreadID);

                    const HANDLE thread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ThreadID);
                    if (thread != nullptr)
                    {
                        PWSTR description = nullptr;
                        if (SUCCEEDED(GetThreadDescription(thread, &description)) && description != nullptr)
                        {
                            if (description[0] != L'\0')
                            {
                                out.Str("  \"");
                                out.Wide(description);
                                out.Ch('"');
                            }
                            LocalFree(description);
                        }
                        CloseHandle(thread);
                    }

                    if (entry.th32ThreadID == faultingThreadId)
                        out.Str("   <<< FAULTING");
                    out.Ch('\n');
                } while (Thread32Next(snapshot, &entry) != FALSE);
            }

            CloseHandle(snapshot);
        }

        void FlushStage(Appender& out) noexcept
        {
            if (g_reportFile == INVALID_HANDLE_VALUE) return;
            const DWORD size = static_cast<DWORD>(out.Length(g_reportBuffer));
            if (size != 0)
            {
                DWORD written = 0;
                WriteFile(g_reportFile, g_reportBuffer, size, &written, nullptr);
                FlushFileBuffers(g_reportFile);
            }
            out.Cur = g_reportBuffer;
        }

        void WriteCrashReport(const CrashJob& job) noexcept
        {
            Appender out{g_reportBuffer, g_reportBuffer + ReportBufferSize - 1};

            SYSTEMTIME now{};
            GetSystemTime(&now);

            out.Str("SRTOverlay shim crash report\n============================\n\n");
            out.Str("Time (UTC)       : ");
            out.Dec(now.wYear); out.Ch('-'); out.Dec2(now.wMonth); out.Ch('-'); out.Dec2(now.wDay);
            out.Ch(' ');
            out.Dec2(now.wHour); out.Ch(':'); out.Dec2(now.wMinute); out.Ch(':'); out.Dec2(now.wSecond);
            out.Str("\nProcess ID       : ");
            out.Dec(GetCurrentProcessId());
            out.Str("\nFaulting thread  : ");
            out.Dec(job.FaultingThreadId);
            out.Str("\nMinidump         : ");
            out.Wide(job.DumpPath);
            if (!job.DumpWritten)
            {
                out.Str("   *** NOT WRITTEN, GetLastError=");
                out.Dec(job.DumpError);
                out.Str(" ***");
            }
            out.Ch('\n');
            FlushStage(out);

            if (job.ExceptionInfo != nullptr && job.ExceptionInfo->ExceptionRecord != nullptr)
            {
                out.Ch('\n');
                AppendExceptionSummary(out, job.ExceptionInfo->ExceptionRecord);
            }
            if (job.ExceptionInfo != nullptr && job.ExceptionInfo->ContextRecord != nullptr)
                AppendRegisters(out, job.ExceptionInfo->ContextRecord);
            FlushStage(out);

            __try
            {
                TakeModuleSnapshot();
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                g_moduleSnapshotCount = 0;
            }

            if (job.ExceptionInfo != nullptr && job.ExceptionInfo->ExceptionRecord != nullptr)
            {
                out.Str("\nFault address    : ");
                AppendResolvedAddress(out, reinterpret_cast<uintptr_t>(job.ExceptionInfo->ExceptionRecord->ExceptionAddress));
                out.Ch('\n');
            }

            AppendFaultingStack(out, job.FaultingThreadId);
            AppendShimState(out);
            FlushStage(out);

            AppendModuleList(out);
            FlushStage(out);

            AppendThreadInventory(out, job.FaultingThreadId);
            out.Str("\n--- End of report ---\n");
            FlushStage(out);
        }

        void AppendCrashLineToLog(const EXCEPTION_RECORD* record, const wchar_t* reportFileName,
                                  bool dumpWritten, DWORD dumpError) noexcept
        {
            char line[1024];
            Appender out{line, line + sizeof(line) - 1};

            out.Str("\nException ");
            out.Hex(record != nullptr ? record->ExceptionCode : 0u, 0);
            out.Str(" occurred at ");
            AppendResolvedAddress(out, record != nullptr ? reinterpret_cast<uintptr_t>(record->ExceptionAddress) : 0u);
            out.Str("\nCrash report: ");
            out.Wide(reportFileName);
            if (!dumpWritten)
            {
                out.Str("\nMiniDumpWriteDump FAILED, GetLastError=");
                out.Dec(dumpError);
            }
            out.Ch('\n');

            AppendToLogFile(line, static_cast<DWORD>(out.Length(line)));
        }

        void WriteDumpAndReport(CrashJob& job) noexcept
        {
            g_faultFrameCount = 0;
            if (job.ExceptionInfo != nullptr && job.ExceptionInfo->ContextRecord != nullptr)
                g_faultFrameCount = UnwindStack(*job.ExceptionInfo->ContextRecord, g_faultFrames, MaxStackFrames);

            BuildExtraMemoryRanges(job.ExceptionInfo != nullptr ? job.ExceptionInfo->ContextRecord : nullptr);

            job.DumpWritten = false;
            job.DumpError = 0;

            const HANDLE dumpFile = CreateFileW(job.DumpPath, GENERIC_WRITE, FILE_SHARE_READ, nullptr,
                                                CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (dumpFile == INVALID_HANDLE_VALUE)
            {
                job.DumpError = GetLastError();
            }
            else
            {
                MINIDUMP_EXCEPTION_INFORMATION exceptionParam{
                    .ThreadId = job.FaultingThreadId,
                    .ExceptionPointers = job.ExceptionInfo,
                    .ClientPointers = FALSE};

                const ULONG dumpType = static_cast<ULONG>(
                    MiniDumpWithDataSegs |
                    MiniDumpWithHandleData |
                    MiniDumpWithUnloadedModules |
                    MiniDumpWithIndirectlyReferencedMemory |
                    MiniDumpWithProcessThreadData |
                    MiniDumpWithThreadInfo |
                    MiniDumpWithFullMemoryInfo |
                    MiniDumpWithModuleHeaders |
                    MiniDumpIgnoreInaccessibleMemory);

                MINIDUMP_CALLBACK_INFORMATION callbackInfo{
                    .CallbackRoutine = &MiniDumpCallback,
                    .CallbackParam = nullptr};

                job.DumpWritten = MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(), dumpFile,
                                                    static_cast<MINIDUMP_TYPE>(dumpType), &exceptionParam,
                                                    nullptr, &callbackInfo) != FALSE;
                if (!job.DumpWritten)
                    job.DumpError = GetLastError();

                CloseHandle(dumpFile);
            }

            __try
            {
                g_reportFile = CreateFileW(job.ReportPath, GENERIC_WRITE, FILE_SHARE_READ, nullptr,
                                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
                WriteCrashReport(job);
                if (g_reportFile != INVALID_HANDLE_VALUE)
                {
                    CloseHandle(g_reportFile);
                    g_reportFile = INVALID_HANDLE_VALUE;
                }
                AppendCrashLineToLog(job.ExceptionInfo != nullptr ? job.ExceptionInfo->ExceptionRecord : nullptr,
                                     job.ReportPath, job.DumpWritten, job.DumpError);
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                if (g_reportFile != INVALID_HANDLE_VALUE)
                {
                    CloseHandle(g_reportFile);
                    g_reportFile = INVALID_HANDLE_VALUE;
                }
                static constexpr char kFallback[] = "\nSRT overlay crash handler: report generation faulted; see the .dmp.\n";
                AppendToLogFile(kFallback, static_cast<DWORD>(sizeof(kFallback) - 1));
            }
        }

        DWORD WINAPI CrashWorkerMain(LPVOID) noexcept
        {
            SetThreadDescription(GetCurrentThread(), L"SRTOverlay Crash Worker");
            ReserveStackForCrashHandling();

            for (;;)
            {
                if (WaitForSingleObject(g_workerStart, INFINITE) != WAIT_OBJECT_0)
                    return 0;

                // Woken to exit (Uninstall) rather than to write a dump.
                if (g_workerShutdown.load(std::memory_order_acquire))
                    return 0;

                __try
                {
                    WriteDumpAndReport(g_job);
                }
                __except (EXCEPTION_EXECUTE_HANDLER)
                {
                }

                SetEvent(g_workerDone);
            }
        }

        LONG WINAPI SRTOverlayUnhandledExceptionFilter(EXCEPTION_POINTERS* exceptionInfo)
        {
            if (g_inHandler.test_and_set(std::memory_order_acquire))
                return EXCEPTION_CONTINUE_SEARCH;

            SYSTEMTIME now{};
            GetSystemTime(&now);

            g_job.ExceptionInfo = exceptionInfo;
            g_job.FaultingThreadId = GetCurrentThreadId();
            g_job.DumpWritten = false;
            g_job.DumpError = 0;
            BuildCrashFileName(now, L".dmp", g_job.DumpPath);
            BuildCrashFileName(now, L".crash.txt", g_job.ReportPath);

            if (g_workerThread != nullptr && g_workerStart != nullptr && g_workerDone != nullptr)
            {
                SetEvent(g_workerStart);
                if (WaitForSingleObject(g_workerDone, CrashWorkTimeoutMs) != WAIT_OBJECT_0)
                {
                    static constexpr char kTimeout[] = "\nSRT overlay crash handler: dump/report timed out; artifacts may be truncated.\n";
                    AppendToLogFile(kTimeout, static_cast<DWORD>(sizeof(kTimeout) - 1));
                }
            }
            else
            {
                __try
                {
                    WriteDumpAndReport(g_job);
                }
                __except (EXCEPTION_EXECUTE_HANDLER)
                {
                }
            }

            // Let REFramework and WER see the exception too.
            return EXCEPTION_CONTINUE_SEARCH;
        }
    }

    // ---- Public surface ----

    void RecordHook(const char* name, void* target) noexcept
    {
        const uint32_t index = g_hookCount.load(std::memory_order_relaxed);
        if (index >= MaxHookRecords || name == nullptr) return;

        size_t i = 0;
        while (i + 1 < sizeof(g_hooks[index].Name) && name[i] != '\0')
        {
            g_hooks[index].Name[i] = name[i];
            ++i;
        }
        g_hooks[index].Name[i] = '\0';
        g_hooks[index].Target = reinterpret_cast<uintptr_t>(target);
        g_hooks[index].Installed = false;
        g_hookCount.store(index + 1, std::memory_order_release);
    }

    void MarkHookInstalled(const char* name, bool installed) noexcept
    {
        if (name == nullptr) return;

        const uint32_t count = g_hookCount.load(std::memory_order_acquire);
        for (uint32_t index = 0; index < count && index < MaxHookRecords; ++index)
        {
            const char* a = g_hooks[index].Name;
            const char* b = name;
            while (*a != '\0' && *a == *b) { ++a; ++b; }
            if (*a == '\0' && *b == '\0')
            {
                g_hooks[index].Installed = installed;
                return;
            }
        }
    }

    void ReserveStackForCrashHandling() noexcept
    {
        ULONG stackBytes = StackGuaranteeBytes;
        SetThreadStackGuarantee(&stackBytes);
    }

    void Install(HMODULE shimModule, const wchar_t* logPath) noexcept
    {
        const auto* selfBase = reinterpret_cast<const uint8_t*>(shimModule);
        g_selfIdentity = selfBase != nullptr ? ReadBuildIdentity(selfBase) : BuildIdentity{};
        const auto* exeBase = reinterpret_cast<const uint8_t*>(GetModuleHandleW(nullptr));
        g_exeIdentity = exeBase != nullptr ? ReadBuildIdentity(exeBase) : BuildIdentity{};

        // Leaf names for the dump whitelist.
        wchar_t shimPath[MAX_PATH]{};
        GetModuleFileNameW(shimModule, shimPath, MAX_PATH);
        CopyWide(g_shimLeaf, MAX_PATH, LeafName(shimPath));

        wchar_t exePath[MAX_PATH]{};
        GetModuleFileNameW(nullptr, exePath, MAX_PATH);
        CopyWide(g_exeLeaf, MAX_PATH, LeafName(exePath));

        // Output directory: the directory of logPath, else the shim's own directory.
        g_outputDir[0] = L'\0';
        g_haveLogPath = false;
        if (logPath != nullptr && logPath[0] != L'\0')
        {
            CopyWide(g_logPath, MAX_PATH, logPath);
            g_haveLogPath = true;

            CopyWide(g_outputDir, MAX_PATH, logPath);
            for (int i = static_cast<int>(wcslen(g_outputDir)) - 1; i >= 0; --i)
            {
                if (g_outputDir[i] == L'\\' || g_outputDir[i] == L'/')
                {
                    g_outputDir[i] = L'\0';
                    break;
                }
                if (i == 0)
                    g_outputDir[0] = L'\0';
            }
        }
        if (g_outputDir[0] == L'\0')
        {
            CopyWide(g_outputDir, MAX_PATH, shimPath);
            for (int i = static_cast<int>(wcslen(g_outputDir)) - 1; i >= 0; --i)
            {
                if (g_outputDir[i] == L'\\' || g_outputDir[i] == L'/')
                {
                    g_outputDir[i] = L'\0';
                    break;
                }
                if (i == 0)
                    g_outputDir[0] = L'\0';
            }
        }

        ReserveStackForCrashHandling();

        // Park the worker now, while healthy: CreateThread at crash time takes the loader lock, one of
        // the locks a crashing thread most likely holds.
        g_workerStart = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        g_workerDone = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (g_workerStart != nullptr && g_workerDone != nullptr)
        {
            g_workerThread = CreateThread(nullptr, 0, &CrashWorkerMain, nullptr, 0, nullptr);
            if (g_workerThread == nullptr)
            {
                CloseHandle(g_workerStart);
                CloseHandle(g_workerDone);
                g_workerStart = nullptr;
                g_workerDone = nullptr;
            }
        }

        g_workerShutdown.store(false);
        g_previousFilter = SetUnhandledExceptionFilter(&SRTOverlayUnhandledExceptionFilter);
    }

    void Uninstall() noexcept
    {
        // Restore whatever filter we replaced, so nothing calls into our code once the module unloads.
        SetUnhandledExceptionFilter(g_previousFilter);
        g_previousFilter = nullptr;

        // Wake the parked worker to exit and join it before the module can be freed.
        if (g_workerThread != nullptr)
        {
            g_workerShutdown.store(true, std::memory_order_release);
            if (g_workerStart != nullptr)
                SetEvent(g_workerStart);
            WaitForSingleObject(g_workerThread, 5000);
            CloseHandle(g_workerThread);
            g_workerThread = nullptr;
        }

        if (g_workerStart != nullptr) { CloseHandle(g_workerStart); g_workerStart = nullptr; }
        if (g_workerDone != nullptr) { CloseHandle(g_workerDone); g_workerDone = nullptr; }
    }
}
