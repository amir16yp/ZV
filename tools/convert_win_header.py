#!/usr/bin/env python3
"""Convert Windows SDK C headers to ZV extern bindings.

Single header:
    python tools/convert_win_header.py \
        --header "C:/Program Files (x86)/Windows Kits/10/Include/10.0.26100.0/um/utilapiset.h" \
        --lib kernel32.dll \
        --out lib/win/kernel32_more.zv \
        --include-types lib/win/types.zv

Single header with auto DLL detection and per-header type aliases:
    python tools/convert_win_header.py \
        --header "C:/Program Files (x86)/Windows Kits/10/Include/10.0.26100.0/um/cfgmgr32.h" \
        --auto-lib \
        --out lib/win/cfgmgr32.zv \
        --include-types types.zv \
        --extract-typedefs

Batch convert all mapped headers in a directory:
    python tools/convert_win_header.py \
        --input-dir "C:/Program Files (x86)/Windows Kits/10/Include/10.0.26100.0/um" \
        --out-dir lib/win \
        --auto-lib \
        --include-types types.zv \
        --extract-typedefs

The converter handles WINAPI-style function declarations, strips common SAL
annotations and calling conventions, maps Windows typedefs to ZV types, and can
extract numeric #define constants and simple typedefs into per-header companion
files. It warns about constructs it cannot translate (varargs, inline functions,
complex macros, etc.).
"""

import argparse
import re
import sys
from pathlib import Path

# Windows C type / typedef -> ZV type.
# Distinct Windows kinds are intentionally mapped to the newtypes declared in
# lib/win/types.zv rather than to raw ZV primitives.
TYPE_MAP = {
    # C primitives (both lower and upper case, since Windows headers use both)
    "void": "VOID",
    "VOID": "VOID",
    "int": "INT32",
    "INT": "INT32",
    "unsigned": "UINT32",
    "unsigned int": "UINT32",
    "UINT": "UINT32",
    "long": "INT32",
    "LONG": "LONG",
    "unsigned long": "UINT32",
    "short": "INT16",
    "SHORT": "SHORT",
    "unsigned short": "UINT16",
    "char": "INT8",
    "unsigned char": "UINT8",
    "INT8": "INT8",
    "INT16": "INT16",
    "INT32": "INT32",
    "INT64": "INT64",
    "UINT8": "UINT8",
    "UINT16": "UINT16",
    "UINT32": "UINT32",
    "UINT64": "UINT64",
    "FLOAT32": "FLOAT32",
    "FLOAT64": "FLOAT64",
    "float": "FLOAT32",
    "FLOAT": "FLOAT32",
    "double": "FLOAT64",
    "DOUBLE": "FLOAT64",
    "LONGLONG": "INT64",
    "ULONGLONG": "UINT64",
    "DWORD64": "UINT64",
    "PDWORD64": "PTR<UINT64>",
    "DWORD_PTR": "SIZE_T",
    "ULONG_PTR": "SIZE_T",
    "LONG_PTR": "SSIZE_T",
    "INT_PTR": "INT_PTR",
    "UINT_PTR": "UINT_PTR",

    # Windows base integer typedefs
    "BOOL": "BOOL32",
    "BOOLEAN": "BOOL32",
    "DWORD": "DWORD",
    "WORD": "WORD",
    "BYTE": "BYTE",
    "ULONG": "ULONG",
    "USHORT": "USHORT",
    "CHAR": "INT8",
    "UCHAR": "UINT8",
    "WCHAR": "UINT16",
    "HRESULT": "HRESULT",
    "COMPUTER_NAME_FORMAT": "UINT32",
    "LOGICAL_PROCESSOR_RELATIONSHIP": "UINT32",
    "THREAD_INFORMATION_CLASS": "UINT32",
    "DEVELOPER_DRIVE_ENABLEMENT_STATE": "UINT32",
    "PROCESS_INFORMATION_CLASS": "UINT32",
    "QUEUE_USER_APC_FLAGS": "UINT32",
    "LPARAM": "LPARAM",
    "WPARAM": "WPARAM",
    "LRESULT": "LRESULT",
    "COLORREF": "COLORREF",
    "SIZE_T": "SIZE_T",
    "SSIZE_T": "SSIZE_T",
    "ATOM": "ATOM",
    "ACCESS_MASK": "DWORD",
    "REGSAM": "DWORD",
    "HFILE": "INT32",
    "LONG32": "INT32",
    "ULONG32": "UINT32",
    "LSTATUS": "LONG",
    "LARGE_INTEGER": "PTR<VOID>",
    "PLARGE_INTEGER": "PTR<VOID>",
    "ULARGE_INTEGER": "PTR<VOID>",
    "PULARGE_INTEGER": "PTR<VOID>",
    "u_long": "ULONG",
    "u_short": "USHORT",
    "u_int": "UINT32",
    "u_char": "UINT8",
    "SOCKET": "UINT_PTR",
    "WSAEVENT": "PTR<VOID>",
    "BSTR": "PTR<VOID>",
    "VARTYPE": "UINT16",
    "LCID": "DWORD",
    "REFGUID": "PTR<VOID>",
    "REFCLSID": "PTR<VOID>",
    "CLSID": "PTR<VOID>",
    "IID": "PTR<VOID>",
    "CY": "PTR<VOID>",
    "DATE": "FLOAT64",
    "VARIANT_BOOL": "INT16",
    "MMRESULT": "UINT32",
    "SYSKIND": "UINT32",
    "REGKIND": "UINT32",
    "DISPID": "INT32",
    "CALLCONV": "UINT32",
    "ASSOCF": "UINT32",
    "SHGLOBALCOUNTER": "UINT32",
    "SFBS_FLAGS": "UINT32",
    "STIF_FLAGS": "UINT32",
    "URLIS": "UINT32",
    "SRRF": "UINT32",
    "SHREGDEL_FLAGS": "UINT32",
    "LANGID": "UINT16",
    "APTTYPE": "UINT32",
    "TA_PROPERTY": "UINT32",
    "THEMESIZE": "UINT32",
    "WINDOWTHEMEATTRIBUTETYPE": "UINT32",
    "DWMTRANSITION_OWNEDWINDOW_TARGET": "UINT32",
    "AgileReferenceOptions": "UINT32",
    "BP_BUFFERFORMAT": "UINT32",
    "SHREGENUM_FLAGS": "UINT32",
    "GESTURE_TYPE": "UINT32",
    "DWM_SHOWCONTACT": "UINT32",
    "ASSOCSTR": "UINT32",
    "ASSOCKEY": "UINT32",
    "D2D1_FACTORY_TYPE": "UINT32",
    "D2D1_POINT_2F": "PTR<VOID>",
    "D2D1_MATRIX_3X2_F": "PTR<VOID>",
    "D3D_BLOB_PART": "UINT32",
    "D3D_DRIVER_TYPE": "UINT32",
    "D3D_ROOT_SIGNATURE_VERSION": "UINT32",
    "D3D_FEATURE_LEVEL": "UINT32",
    "XAUDIO2_PROCESSOR": "UINT32",

    # Handles
    "HANDLE": "HANDLE",
    "HINSTANCE": "HINSTANCE",
    "HMODULE": "HMODULE",
    "HWND": "HWND",
    "HDC": "HDC",
    "HICON": "HICON",
    "HCURSOR": "HCURSOR",
    "HBRUSH": "HBRUSH",
    "HPEN": "HPEN",
    "HFONT": "HFONT",
    "HMENU": "HMENU",
    "HBITMAP": "HBITMAP",
    "HGLOBAL": "HGLOBAL",
    "HLOCAL": "HLOCAL",
    "HKL": "HKL",
    "HDESK": "HDESK",
    "HWINSTA": "HWINSTA",
    "HHOOK": "HHOOK",
    "HACCEL": "HACCEL",
    "HMONITOR": "HMONITOR",
    "HRGN": "HRGN",
    "HDWP": "HDWP",
    "HDEVNOTIFY": "HDEVNOTIFY",
    "HPOWERNOTIFY": "HPOWERNOTIFY",
    "HTOUCHINPUT": "HTOUCHINPUT",
    "HSYNTHETICPOINTERDEVICE": "HSYNTHETICPOINTERDEVICE",
    "HGESTUREINFO": "HGESTUREINFO",
    "HRAWINPUT": "HRAWINPUT",
    "HDROP": "HDROP",
    "HKEY": "HKEY",
    "HRSRC": "PTR<VOID>",
    "HGDIOBJ": "PTR<VOID>",
    "HMETAFILE": "PTR<VOID>",
    "HENHMETAFILE": "PTR<VOID>",
    "HPALETTE": "PTR<VOID>",
    "HCOLORSPACE": "PTR<VOID>",
    "HGLRC": "PTR<VOID>",
    "HWINEVENTHOOK": "PTR<VOID>",
    "HRAWINPUTDEVICE": "PTR<VOID>",
    "HTHEME": "PTR<VOID>",
    "HPAINTBUFFER": "PTR<VOID>",
    "HANIMATIONBUFFER": "PTR<VOID>",
    "HUSKEY": "PTR<VOID>",
    "DLL_DIRECTORY_COOKIE": "PTR<VOID>",
    "CO_MTA_USAGE_COOKIE": "PTR<VOID>",
    "CO_DEVICE_CATALOG_COOKIE": "PTR<VOID>",
    "RPC_AUTH_IDENTITY_HANDLE": "PTR<VOID>",
    "HIMAGELIST": "PTR<VOID>",
    "HTHUMBNAIL": "PTR<VOID>",
    "REFIID": "PTR<VOID>",
    "SHSTOCKICONID": "UINT32",

    # Strings
    "LPSTR": "LPSTR",
    "PSTR": "LPSTR",
    "LPCSTR": "LPCSTR",
    "PCSTR": "LPCSTR",
    "LPWSTR": "WSTRING",
    "PWSTR": "WSTRING",
    "LPCWSTR": "WSTRING",
    "PCWSTR": "WSTRING",
    "LPCH": "LPSTR",
    "PCH": "LPSTR",
    "LPCCH": "LPCSTR",

    # Common pointer typedefs (mapped to opaque pointers for simple bindings)
    "LPBOOL": "PTR<BOOL32>",
    "PBOOL": "PTR<BOOL32>",
    "LPDWORD": "PTR<DWORD>",
    "PDWORD": "PTR<DWORD>",
    "LPHANDLE": "PTR<HANDLE>",
    "PHANDLE": "PTR<HANDLE>",
    "LPWORD": "PTR<WORD>",
    "PWORD": "PTR<WORD>",
    "LPBYTE": "PTR<BYTE>",
    "PBYTE": "PTR<BYTE>",
    "LPINT": "PTR<INT32>",
    "PINT": "PTR<INT32>",
    "LPUINT": "PTR<UINT32>",
    "PUINT": "PTR<UINT32>",
    "PLONG": "PTR<LONG>",
    "PULONG": "PTR<ULONG>",
    "LPSECURITY_ATTRIBUTES": "PTR<VOID>",
    "PSECURITY_ATTRIBUTES": "PTR<VOID>",
    "LPFILETIME": "PTR<VOID>",
    "PFILETIME": "PTR<VOID>",
    "LPVOID": "PTR<VOID>",
    "LPCVOID": "PTR<VOID>",

    # Pointers
    "PVOID": "PTR<VOID>",
    "LPCVOID": "PTR<VOID>",
    "FARPROC": "PTR<VOID>",
    "NEARPROC": "PTR<VOID>",
    "PROC": "PTR<VOID>",

    # varargs
    "va_list": "PTR<VOID>",
    "VA_LIST": "PTR<VOID>",

    # Common Windows enum/flag typedefs -> underlying integer type
    "FEEDBACK_TYPE": "UINT32",
    "DIALOG_CONTROL_DPI_CHANGE_BEHAVIORS": "UINT32",
    "DIALOG_DPI_CHANGE_BEHAVIORS": "UINT32",
    "DPI_AWARENESS_CONTEXT": "PTR<VOID>",
    "DPI_AWARENESS": "UINT32",
    "DPI_HOSTING_BEHAVIOR": "UINT32",
    "ORIENTATION_PREFERENCE": "UINT32",
    "TOOLTIP_DISMISS_FLAGS": "UINT32",
    "MOVESIZE_OPERATION": "UINT32",
    "EXECUTION_STATE": "DWORD",
    "LATENCY_TIME": "DWORD",
    "DEP_SYSTEM_POLICY_TYPE": "UINT32",
    "GET_FILEEX_INFO_LEVELS": "UINT32",
    "FINDEX_INFO_LEVELS": "UINT32",
    "STREAM_INFO_LEVELS": "UINT32",
    "FINDEX_SEARCH_OPS": "UINT32",
    "CONFIGRET": "DWORD",
    "RETURN_TYPE": "DWORD",
    "AUDIT_EVENT_TYPE": "UINT32",
    "SECURITY_INFORMATION": "DWORD",
    "UMS_THREAD_INFO_CLASS": "UINT32",
    "READ_DIRECTORY_NOTIFY_INFORMATION_CLASS": "UINT32",
    "FILE_INFO_BY_HANDLE_CLASS": "UINT32",
    "FILE_INFO_BY_NAME_CLASS": "UINT32",
    "WAITORTIMERCALLBACK": "PTR<VOID>",
    "APPLICATION_RECOVERY_CALLBACK": "PTR<VOID>",
    "BLENDFUNCTION": "UINT32",
    "DWORDLONG": "UINT64",
    "ULONG64": "UINT64",
    "LONG64": "INT64",
    "FLOAT": "FLOAT32",
    "DOUBLE": "FLOAT64",
    "DIRECTORY_FLAGS": "UINT32",
    "MEMORY_RESOURCE_NOTIFICATION_TYPE": "UINT32",
    "OFFER_PRIORITY": "UINT32",
    "WIN32_MEMORY_INFORMATION_CLASS": "UINT32",
    "WIN32_MEMORY_PARTITION_INFORMATION_CLASS": "UINT32",
    "WSAESETSERVICEOP": "UINT32",
    "in_addr": "DWORD",
    "REASON_CONTEXT": "PTR<VOID>",
    "DEVINST": "DWORD",
    "DEVNODE": "DWORD",
    "DEVINSTID_A": "LPCSTR",
    "DEVINSTID_W": "WSTRING",
    "DEVPROPTYPE": "UINT32",
    "REGDISPOSITION": "UINT32",
    "RANGE_LIST": "PTR<VOID>",
    "LOG_CONF": "PTR<VOID>",
    "RES_DES": "PTR<VOID>",
    "RESOURCEID": "UINT32",
    "HMACHINE": "PTR<VOID>",
    "HCMNOTIFICATION": "PTR<VOID>",
    "CONFLICT_LIST": "PTR<VOID>",
    "PNP_VETO_TYPE": "UINT32",

    "LPMCMTCERTPICKINFO": "PTR<VOID>",
    "LPCMC_PROFILE_INFO_DATA": "PTR<VOID>",
    "LPSTR_PROPSPEC": "PTR<VOID>",
    "LPWSTR_PROPSPEC": "PTR<VOID>",
    "PCNZCH": "LPCSTR",
    "PCNZWCH": "LPCWSTR",
    "GROUP": "DWORD",
    "SCODE": "HRESULT",
    "OLECHAR": "UINT16",
    "LPOLESTR": "WSTRING",
    "LPCOLESTR": "WSTRING",
}

# Annotation / calling-convention tokens to strip.
ANNOTATIONS = {
    "WINAPI", "WINAPIV", "CALLBACK", "APIENTRY", "WINAPI_INLINE",
    "WINBASEAPI", "WINUSERAPI", "WINABLEAPI", "WINGDIAPI", "SHELLAPI",
    "WINSPOOLAPI", "WINSOCK_API_LINKAGE",
    "WSAAPI", "PASCAL", "CMAPI", "CMAPI_INLINE",
    "WINOLEAPI", "WINOLEAPI_INLINE",
    "THEMEAPI", "DWMAPI", "XAUDIO2_STDAPI",
    "WINMMAPI",
    "DECLSPEC_IMPORT", "DECLSPEC_DEPRECATED", "DECLSPEC_NORETURN",
    "extern",
    "DECLSPEC_ALLOCATOR", "DECLSPEC_GUARDSUPPRESS",
    "__drv_aliasesMem",
    "_In_", "_Out_", "_Inout_", "_In_opt_", "_Out_opt_", "_Inout_opt_",
    "_In_reads_", "_Out_writes_", "_In_reads_bytes_", "_Out_writes_bytes_",
    "_In_reads_opt_", "_Out_writes_opt_", "_Inout_updates_", "_Inout_updates_opt_",
    "_Inout_updates_all_", "_Out_writes_all_", "_Out_writes_bytes_to_",
    "_Out_writes_to_", "_Out_writes_to_opt_", "_Inout_updates_all_opt_",
    "_Ret_maybenull_", "_Ret_writes_", "_Ret_writes_bytes_", "_Ret_z_",
    "_Ret_opt_", "_Ret_notnull_", "_Ret_range_", "_Ret_writes_z_",
    "_Check_return_", "_Success_", "_Must_inspect_result_", "_Use_decl_annotations_",
    "_Post_", "_Pre_", "_Reserved_", "_In_z_", "_Inout_z_", "_Out_z_",
    "_In_reads_z_", "_Out_writes_z_", "_In_reads_opt_z_", "_Out_writes_opt_z_",
    "_In_opt_z_", "_Out_opt_z_", "_Inout_z_",
    "_Null_terminated_", "_Notnull_", "_Maybe_raises_SEH_exception_",
    "_Frees_ptr_", "_Frees_ptr_opt_", "_Outptr_", "_Outptr_result_maybenull_", "_COM_Outptr_",
    "_Outptr_opt_", "_Outptr_result_buffer_", "_Outptr_result_bytebuffer_",
    "_When_", "_At_", "_Analysis_assume_", "_Analysis_noreturn_",
    "_Post_equals_last_error_", "_Post_ptr_invalid_", "_Post_notnull_",
    "_Post_maybenull_", "_Post_satisfies_", "_Post_readable_size_",
    "_Post_valid_", "_Post_invalid_", "_Post_writable_size_",
    "_Pre_null_", "_Pre_notnull_", "_Pre_maybenull_", "_Pre_satisfies_",
    "__in", "__out", "__inout", "__in_opt", "__out_opt", "__inout_opt",
    "__reserved", "__callback", "__forceinline", "__stdcall", "__cdecl",
    "FORCEINLINE", "_CRTIMP", "__declspec(dllimport)",
    "NTSYSAPI", "NTAPI", "WINADVAPI", "NTHALAPI",
    "NOT_BUILD_WINDOWS_DEPRECATE",
    "IN", "OUT", "OPT", "OPTIONAL",
    "__analysis_noreturn",
    "_WINSOCK_DEPRECATED",
    "_NullNull_terminated_",
    "FAR", "NEAR", "__ptr64", "__ptr32", "__unaligned", "volatile",
    "_CONST_RETURN", "CONST", "const",
    "__attribute__",
    "_Printf_format_string_",
    "__drv_aliasesMem", "aliasesMem",
}


def strip_comments(text: str) -> str:
    """Remove C/C++ block and line comments while preserving line numbers roughly."""
    # block comments /* ... */
    text = re.sub(r"/\*.*?\*/", " ", text, flags=re.DOTALL)
    # line comments // ...
    text = re.sub(r"//.*?$", "", text, flags=re.MULTILINE)
    return text


def _remove_balanced_parens(text: str, prefix: str) -> str:
    """Remove the first occurrence of prefix(...) handling nested parentheses.

    If a '(' is not adjacent to the prefix, only the prefix token is removed.
    """
    i = text.find(prefix)
    if i == -1:
        return text
    after = i + len(prefix)
    # Look for an adjacent '(' (allowing only whitespace between prefix and '(').
    m = re.match(r"\s*\(", text[after:])
    if not m:
        return text[:i] + " " + text[after:]
    start = after + m.end() - 1
    depth = 1
    j = start + 1
    while j < len(text) and depth > 0:
        if text[j] == "(":
            depth += 1
        elif text[j] == ")":
            depth -= 1
        j += 1
    return text[:i] + " " + text[j:]


def _extract_balanced_api_marker(text: str, marker: str) -> tuple[str, str] | None:
    """Locate `marker(...)` in text and return (before, content, after)."""
    i = text.find(marker)
    if i == -1:
        return None
    start = text.find("(", i)
    if start == -1:
        return None
    depth = 1
    j = start + 1
    while j < len(text) and depth > 0:
        if text[j] == "(":
            depth += 1
        elif text[j] == ")":
            depth -= 1
        j += 1
    content = text[start + 1 : j - 1].strip()
    return text[:i], content, text[j:]


def strip_annotation_expressions(text: str) -> str:
    """Remove SAL/declspec annotations that contain parentheses."""
    # API marker macros that embed the return type. Forms like SHSTDAPI_(type)
    # expand to `type`; bare forms like SHSTDAPI expand to HRESULT. Markers may
    # contain nested SAL annotations inside the return-type expression, so we
    # strip them with balanced parentheses.
    api_markers = [
        "SHSTDAPI_", "STDAPI_", "LWSTDAPI_", "LWSTDAPIV_", "HRESULTV_",
        "WINOLEAUTAPI_", "WINOLEAPI_", "THEMEAPI_", "DWMAPI_", "XAUDIO2_STDAPI_",
    ]
    # Process longer, more specific markers first so that e.g. LWSTDAPI_ is not
    # partially consumed as STDAPI_ inside it.
    for marker in sorted(api_markers, key=len, reverse=True):
        while True:
            result = _extract_balanced_api_marker(text, marker)
            if result is None:
                break
            before, content, after = result
            text = before + " " + content + " " + after

    # Bare API markers without an argument expand to HRESULT.
    bare_api_markers = [
        "SHSTDAPI", "STDAPI", "LWSTDAPI", "LWSTDAPIV", "HRESULTV",
        "WINOLEAUTAPI", "WINOLEAPI", "THEMEAPI", "DWMAPI", "XAUDIO2_STDAPI",
    ]
    for marker in sorted(bare_api_markers, key=len, reverse=True):
        text = re.sub(rf"\b{marker}\b", "HRESULT", text)

    # Remove annotations that may contain nested parentheses:
    # _Out_writes_(_Inexpressible_(...)), _Success_(...), etc.
    for prefix in ["_When_", "_At_", "_Success_", "_On_failure_",
                     "_WINSOCK_DEPRECATED_BY", "__out_data_source",
                     "__control_entrypoint", "X2DEFAULT", "__in_bcount",
                     "__out_bcount", "__inout_bcount"]:
        while prefix in text:
            text = _remove_balanced_parens(text, prefix)

    # Generic SAL annotations with balanced parentheses: _Name_(...).
    # The open parenthesis must be on the same line as the annotation name so
    # we do not accidentally consume unrelated text across line breaks.
    while True:
        m = re.search(r"_[A-Za-z][A-Za-z0-9_]*_[ \t]*\(", text)
        if not m:
            break
        text = _remove_balanced_parens(text, text[m.start():m.end() - 1].rstrip())

    # __drv_ annotations that take arguments, e.g. __drv_preferredFunction(...)
    while True:
        m = re.search(r"__drv_[A-Za-z0-9_]+[ \t]*\(", text)
        if not m:
            break
        text = _remove_balanced_parens(text, text[m.start():m.end() - 1].rstrip())

    # __declspec(...) and __attribute__((...))
    for marker in ["__declspec", "__attribute__"]:
        while marker in text:
            text = _remove_balanced_parens(text, marker)

    return text


def strip_annotations(token_line: str) -> str:
    """Remove SAL/calling-convention tokens from a line of tokens."""
    parts = token_line.split()
    filtered = [p for p in parts if p not in ANNOTATIONS]
    return " ".join(filtered)


def normalize_type(c_type: str) -> str:
    """Convert a C type string to a ZV type string."""
    c_type = c_type.strip()

    # Strip common qualifiers (volatile, const, FAR, NEAR, __ptr64, struct, enum, union).
    c_type = re.sub(r"\b(const|volatile|FAR|NEAR|__ptr64|__ptr32|__unaligned|struct|enum|union)\b", "", c_type)
    c_type = re.sub(r"\s+", " ", c_type).strip()

    if not c_type:
        return None

    # Exact map match
    if c_type in TYPE_MAP:
        return TYPE_MAP[c_type]

    # const-qualified pointer: const T * -> PTR<T>
    m = re.match(r"^const\s+([\w\s]+?)\s*\*\s*$", c_type)
    if m:
        inner = normalize_type(m.group(1))
        if inner is None or inner == "VOID":
            return "PTR<VOID>"
        return f"PTR<{inner}>"

    # pointer: T * -> PTR<T>
    m = re.match(r"^([\w\s]+?)\s*\*\s*$", c_type)
    if m:
        inner = normalize_type(m.group(1))
        if inner is None or inner == "VOID":
            return "PTR<VOID>"
        return f"PTR<{inner}>"

    # const T &
    m = re.match(r"^const\s+([\w\s]+?)\s*&\s*$", c_type)
    if m:
        return normalize_type(m.group(1) + "*")

    # T & -> PTR<T>
    m = re.match(r"^([\w\s]+?)\s*&\s*$", c_type)
    if m:
        return normalize_type(m.group(1) + "*")

    # arrays: T name[N] -> PTR<T>
    m = re.match(r"^([\w\s]+?)\s+\w+\s*\[.*\]\s*$", c_type)
    if m:
        inner = normalize_type(m.group(1))
        if inner is None or inner == "VOID":
            return "PTR<VOID>"
        return f"PTR<{inner}>"

    # bare array type T[] -> PTR<T>
    m = re.match(r"^([\w\s]+?)\s*\[\]\s*$", c_type)
    if m:
        inner = normalize_type(m.group(1))
        if inner is None or inner == "VOID":
            return "PTR<VOID>"
        return f"PTR<{inner}>"

    # Direct pointer forms like 'SOME_STRUCT*' can be treated as opaque pointers
    # for simple extern declarations.
    if "*" in c_type:
        return "PTR<VOID>"

    # Most remaining Windows typedefs that start with P/LP are pointers to structs.
    # Treat them as opaque pointers rather than failing; this covers things like
    # LPTHREAD_START_ROUTINE, PAPCFUNC, PPROCESSOR_NUMBER, etc.
    if re.match(r"^(LP|PP?)[A-Z]", c_type):
        return "PTR<VOID>"

    # Windows callback function-pointer typedefs commonly end in PROC/FUNC/PROCA/PROCW.
    if re.match(r"^[A-Z].*(PROC|FUNC)[AW]?$", c_type):
        return "PTR<VOID>"

    # Unknown primitive-looking types fall back to a warning.
    return None


def split_params(param_text: str) -> list[str]:
    """Split a parameter list on commas, ignoring commas inside angle brackets."""
    params = []
    depth = 0
    current = []
    for ch in param_text:
        if ch == "<":
            depth += 1
        elif ch == ">":
            depth -= 1
        elif ch == "," and depth == 0:
            params.append("".join(current))
            current = []
            continue
        current.append(ch)
    if current:
        params.append("".join(current))
    return [p.strip() for p in params if p.strip()]


# ZV keywords that are legal C identifiers but cannot be used as parameter names.
RESERVED_PARAM_NAMES = {
    "type", "newtype", "struct", "if", "else", "while", "for", "return",
    "break", "continue", "try", "catch", "throw", "move", "copy", "free",
    "true", "false", "null", "extern", "as", "packed", "const",
}


def sanitize_param_name(name: str) -> str:
    """Escape parameter names that collide with ZV reserved words."""
    if name in RESERVED_PARAM_NAMES:
        return name + "_"
    return name


def parse_param(param: str) -> tuple[str, str] | None:
    """Parse one parameter into (zv_type, param_name)."""
    param = param.strip()
    if not param:
        return None
    if param.lower() == "void":
        return None  # empty param list
    if param == "...":
        return ("UNSUPPORTED_VARARGS", "")

    # Strip annotations from the parameter.
    param = strip_annotation_expressions(param)
    param = strip_annotations(param)

    # Try to split type and name. The name is the last identifier token.
    # Preserve pointer stars attached to the type.
    tokens = param.split()
    if not tokens:
        return None

    # Find last identifier that could be the parameter name.
    name = None
    type_tokens = []
    i = len(tokens) - 1
    while i >= 0:
        t = tokens[i]
        if t in ("*", "&"):
            # pointer token belongs to type
            type_tokens.insert(0, t)
            i -= 1
            continue
        if re.match(r"^[A-Za-z_][A-Za-z0-9_]*$", t) and name is None:
            name = t
            i -= 1
            continue
        break

    type_tokens = tokens[:i + 1] + type_tokens
    c_type = " ".join(type_tokens).strip()

    # If no type tokens remain, the last identifier was the type (unnamed parameter).
    if not c_type and name is not None:
        c_type = name
        name = None

    if name is None:
        name = ""

    zv_type = normalize_type(c_type)
    name = sanitize_param_name(name)
    if zv_type is None:
        return (f"UNSUPPORTED_TYPE({c_type})", name)
    return (zv_type, name)


def parse_function_decl(decl: str) -> dict | None:
    """Parse a single normalized function declaration."""
    # Match: return-stuff name ( params ) ;
    m = re.match(r"^(.+?)\s+(\w+)\s*\((.*?)\)\s*;\s*$", decl, re.DOTALL)
    if not m:
        return None

    raw_return = strip_annotations(m.group(1)).strip()
    name = m.group(2)
    params_text = m.group(3)

    # Skip obvious non-functions / macros
    if name in ANNOTATIONS or name.startswith("__"):
        return None

    zv_return = normalize_type(raw_return)
    if zv_return is None:
        return {"name": name, "return": f"UNSUPPORTED_TYPE({raw_return})", "params": []}

    params = []
    skipped_reason = None
    for p in split_params(params_text):
        parsed = parse_param(p)
        if parsed is None:
            continue
        zv_type, pname = parsed
        if "UNSUPPORTED" in zv_type:
            skipped_reason = zv_type
            break
        params.append((zv_type, pname or f"arg{len(params)}"))

    if skipped_reason:
        return {"name": name, "skipped": skipped_reason}

    return {"name": name, "return": zv_return, "params": params}


def _join_logical_lines(text: str) -> list[str]:
    """Join physical lines using C line-continuation backslashes."""
    lines = text.splitlines()
    logical = []
    current = ""
    for line in lines:
        if line.rstrip().endswith("\\"):
            current += line.rstrip()[:-1].strip() + " "
        else:
            current += line.strip()
            logical.append(current)
            current = ""
    if current:
        logical.append(current)
    return logical


def extract_functions(text: str) -> tuple[list[dict], list[str]]:
    """Extract function declarations and warnings from header text."""
    text = strip_comments(text)
    text = strip_annotation_expressions(text)

    # Collapse multi-line declarations onto logical chunks. Keep preprocessor
    # directives on their own lines so they don't bleed into declarations.
    lines = _join_logical_lines(text)
    chunks = []
    current = ""
    i = 0
    while i < len(lines):
        s = lines[i].strip()
        if not s:
            i += 1
            continue
        if s.startswith("#"):
            if current:
                chunks.append(current)
                current = ""
            chunks.append(s)
            i += 1
            continue
        current = current + " " + s if current else s
        if ";" in current:
            chunks.append(current)
            current = ""
            i += 1
            continue
        # An open brace following a ')' is an inline function body. Merge it
        # so the entire inline definition is skipped by the '{' filter below.
        if current.rstrip().endswith(")"):
            j = i + 1
            while j < len(lines) and not lines[j].strip():
                j += 1
            if j < len(lines) and lines[j].strip().startswith("{"):
                depth = 0
                while j < len(lines):
                    body_line = lines[j].strip()
                    current = current + " " + body_line
                    depth += body_line.count("{") - body_line.count("}")
                    j += 1
                    if depth <= 0:
                        break
                chunks.append(current)
                current = ""
                i = j
                continue
        i += 1
    if current:
        chunks.append(current)

    functions = []
    warnings = []
    seen_function_names = set()
    for chunk in chunks:
        # Skip preprocessor directives, struct/union definitions, inline bodies, etc.
        chunk = chunk.strip()
        if chunk.startswith("#"):
            continue
        if chunk.startswith("typedef"):
            continue
        # Skip function-pointer typedefs that slip through line joining.
        if re.search(r"\(\s*\*\s*\w+", chunk):
            continue
        # Skip COM vtable method declarations.
        if "DECLSPEC_XFGVIRT" in chunk:
            continue
        if "Vtbl" in chunk:
            continue
        if "{" in chunk:
            continue
        if "(" not in chunk or ";" not in chunk:
            continue

        # Must contain a WINAPI-style marker or look like a function declaration.
        # We accept anything with an identifier followed by ( params );
        parsed = parse_function_decl(chunk)
        if parsed is None:
            continue

        if "skipped" in parsed:
            warnings.append(f"skipped '{parsed['name']}': {parsed['skipped']}")
            continue
        if parsed["return"].startswith("UNSUPPORTED"):
            warnings.append(f"skipped '{parsed['name']}': {parsed['return']}")
            continue

        if parsed["name"] in seen_function_names:
            continue
        seen_function_names.add(parsed["name"])
        functions.append(parsed)

    return functions, warnings


def _normalize_constant_type(c_type: str) -> str:
    """Map a C type used in a constant cast to a ZV type."""
    c_type = c_type.strip()
    if c_type in TYPE_MAP:
        return TYPE_MAP[c_type]
    return c_type


def _is_zv_pointer_type(zv_type: str) -> bool:
    """Return True if zv_type is a pointer-ish type better expressed as a `type` alias."""
    return zv_type.startswith("PTR<") or zv_type in (
        "CSTRING", "WSTRING", "LPSTR", "LPCSTR", "LPWSTR", "LPCWSTR",
        "PSTR", "PCSTR", "PWSTR", "PCWSTR",
    )


def extract_typedefs(text: str) -> list[tuple[str, str, bool]]:
    """Extract simple typedefs as ZV newtypes or type aliases.

    Only typedefs of the form `typedef BASE_TYPE NEW_TYPE;` are extracted.
    Function-pointer, struct/union, and multi-line typedefs are skipped.
    Types already present in TYPE_MAP are not re-emitted.
    """
    text = strip_comments(text)
    text = strip_annotation_expressions(text)
    lines = _join_logical_lines(text)
    typedefs = []
    seen = set()
    for line in lines:
        line = line.strip()
        if not line.startswith("typedef"):
            continue
        # Skip struct/union definitions and function-pointer typedefs.
        if "{" in line or re.search(r"\(\s*\*", line):
            continue
        # Single-name typedef: typedef BASE NAME;
        m = re.match(r"^typedef\s+(.+?)\s+(\w+)\s*;\s*$", line)
        names: list[str] = []
        base = ""
        if m:
            base = m.group(1).strip()
            names = [m.group(2).strip()]
        else:
            # Multi-name typedef: typedef BASE A, B, C;
            m2 = re.match(r"^typedef\s+(.+?)\s+((?:\w+\s*,\s*)+\w+)\s*;\s*$", line)
            if m2:
                base = m2.group(1).strip()
                names = [n.strip() for n in m2.group(2).split(",")]
        if not names:
            continue
        for name in names:
            if name in seen or name in TYPE_MAP:
                continue
            zv_type = normalize_type(base)
            if not zv_type:
                continue
            is_newtype = not _is_zv_pointer_type(zv_type)
            typedefs.append((name, zv_type, is_newtype))
            seen.add(name)
    return typedefs


def render_typedefs(typedefs: list[tuple[str, str, bool]]) -> str:
    """Render extracted typedefs as ZV newtype/type declarations."""
    if not typedefs:
        return ""
    lines = ["// Header-specific type aliases extracted from source header"]
    for name, zv_type, is_newtype in typedefs:
        keyword = "newtype" if is_newtype else "type"
        lines.append(f"{keyword} {name} = {zv_type};")
    return "\n".join(lines)


# Macro-like identifiers that should never be treated as numeric constants.
_NON_CONSTANT_MACROS = {
    "DECLSPEC_IMPORT", "DECLSPEC_EXPORT", "EXTERN_C", "NOMINMAX",
    "UNICODE", "_UNICODE", "WIN32", "_WIN32", "_WIN64", "_CRTIMP",
    "MAKEINTRESOURCE", "MAKEINTRESOURCEA", "MAKEINTRESOURCEW",
    "WINSHELLAPI", "SHSTDAPI", "LWSTDAPI", "LWSTDAPIV",
    "WINOLEAUTAPI", "XAUDIO2_STDAPI",
    "PURE", "BEGIN_INTERFACE", "THIS_", "_WINSOCKAPI_",
}
_NON_CONSTANT_MACROS.update(ANNOTATIONS)


def _strip_int_suffixes(value: str) -> str:
    """Remove C integer literal suffixes (U, L, LL, etc.) from a value string."""
    # Repeatedly strip suffixes after hex/decimal/octal literals, even inside
    # parentheses, e.g. (0x00000000L) -> (0x00000000).
    return re.sub(r"(?<=[0-9A-Fa-fxX])([uUlL]+)\b", "", value)


def _is_simple_value(value: str) -> bool:
    """Return True if the value is a number or a simple numeric expression."""
    # Must contain at least one numeric literal (hex, decimal, or octal).
    # This excludes function-name aliases like `#define FooA FooW`.
    if not re.search(r"\b(0[xX][0-9A-Fa-f]+|[0-9]+)\b", value):
        return False
    return bool(re.match(r"^[0-9A-Fa-fxX_\(\)\|\&\~\<\>\+\-\*\/\s,]+$", value))


def _parse_define_line(line: str) -> tuple[str, str] | None:
    """Return (name, raw_value) for a #define line, or None."""
    m = re.match(r"^\s*#define\s+([A-Za-z_][A-Za-z0-9_]*)\s+(.+?)\s*$", line)
    if not m:
        return None
    return m.group(1), m.group(2).strip()


def _process_constant_value(raw_value: str) -> tuple[str, str] | None:
    """Normalize a constant value and return (zv_type, value), or None if not numeric."""
    zv_type = "UINT32"
    value = raw_value

    # Strip a leading type cast: (DWORD)0x..., (UINT)123, etc.
    cast_m = re.match(
        r"^\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)\s*(.+)$",
        value,
    )
    if cast_m:
        zv_type = _normalize_constant_type(cast_m.group(1))
        value = cast_m.group(2).strip()

    # Drop C integer literal suffixes from all numeric literals.
    value = _strip_int_suffixes(value)

    if not _is_simple_value(value):
        return None
    return zv_type, value


def extract_constants(text: str) -> tuple[list[tuple[str, str, str]], list[str]]:
    """Extract numeric #define constants and simple aliases.

    Returns a list of (name, zv_type, value).  Typed casts like
    `#define X (DWORD)0x1234` are split into type and value.
    """
    text = strip_comments(text)

    # First pass: collect all numeric/expression constants.
    numeric_constants: dict[str, tuple[str, str]] = {}
    aliases: dict[str, str] = {}  # name -> raw value (single identifier)
    warnings: list[str] = []

    for line in text.splitlines():
        parsed = _parse_define_line(line)
        if parsed is None:
            continue
        name, raw_value = parsed

        if name in _NON_CONSTANT_MACROS:
            continue

        processed = _process_constant_value(raw_value)
        if processed is not None:
            numeric_constants[name] = processed
            continue

        # Single identifier aliases are resolved in a second pass.
        if re.match(r"^[A-Za-z_][A-Za-z0-9_]*$", raw_value):
            aliases[name] = raw_value
        else:
            warnings.append(f"skipped define '{name}': non-numeric value")

    # Resolve aliases against numeric constants repeatedly until fixed point.
    changed = True
    while changed:
        changed = False
        for name, target in list(aliases.items()):
            if target in numeric_constants:
                zv_type, value = numeric_constants[target]
                numeric_constants[name] = (zv_type, value)
                del aliases[name]
                changed = True

    for name in aliases:
        warnings.append(f"skipped define '{name}': non-numeric value")

    constants = [(name, t, v) for name, (t, v) in numeric_constants.items()]
    return constants, warnings


def extract_macro_functions(text: str) -> list[tuple[str, str, list[str], str]]:
    """Extract well-known function-like macros and return ZV function tuples.

    Each tuple is (name, return_type, param_decls, body).
    """
    text = strip_comments(text)
    found: list[tuple[str, str, list[str], str]] = []
    seen = set()
    for line in text.splitlines():
        m = re.match(r"^\s*#define\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(([^)]*)\)", line)
        if not m:
            continue
        name, args = m.group(1), m.group(2)
        if name in seen or name not in MACRO_FUNCTION_TEMPLATES:
            continue
        # Ensure the number of macro parameters matches the template.
        template = MACRO_FUNCTION_TEMPLATES[name]
        arg_count = len([a for a in args.split(",") if a.strip()]) if args.strip() else 0
        if arg_count != len(template[1]):
            continue
        seen.add(name)
        found.append((name, template[0], template[1], template[2]))
    return found


def render_macro_functions(macro_functions: list[tuple[str, str, list[str], str]]) -> str:
    """Render macro functions as ZV function definitions."""
    lines = ["// Macro functions extracted from header"]
    for name, ret_type, params, body in macro_functions:
        lines.append(f"{ret_type} {name}({', '.join(params)}) {{")
        lines.append(f"    {body}")
        lines.append("}")
    return "\n".join(lines)


def render_extern_block(lib_name: str, functions: list[dict]) -> str:
    """Render the ZV extern block."""
    lines = [f'extern "{lib_name}" {{']
    for fn in functions:
        params = ", ".join(f"{t} {n}" for t, n in fn["params"])
        lines.append(f"    {fn['return']} {fn['name']}({params});")
    lines.append("}")
    return "\n".join(lines)


def render_constants(constants: list[tuple[str, str, str]]) -> str:
    """Render numeric constants as ZV #define directives."""
    lines = ["// Numeric constants extracted from header"]
    for name, _zv_type, value in constants:
        # Some values are expressions; treat them as-is and let the user clean up.
        lines.append(f"#define {name} {value}")
    return "\n".join(lines)


HEADER_DLL_MAP = {
    "handleapi": "kernel32.dll",
    "processthreadsapi": "kernel32.dll",
    "synchapi": "kernel32.dll",
    "sysinfoapi": "kernel32.dll",
    "errhandlingapi": "kernel32.dll",
    "debugapi": "kernel32.dll",
    "utilapiset": "kernel32.dll",
    "fileapi": "kernel32.dll",
    "memoryapi": "kernel32.dll",
    "libloaderapi": "kernel32.dll",
    "processenv": "kernel32.dll",
    "winbase": "kernel32.dll",
    "winuser": "user32.dll",
    "winuser2": "user32.dll",
    "wingdi": "gdi32.dll",
    "shellapi": "shell32.dll",
    "shlwapi": "shlwapi.dll",
    "winreg": "advapi32.dll",
    "winsock2": "ws2_32.dll",
    "winsock": "ws2_32.dll",
    "oleauto": "oleaut32.dll",
    "combaseapi": "ole32.dll",
    "cfgmgr32": "cfgmgr32.dll",
    "winver": "version.dll",
    "dwmapi": "dwmapi.dll",
    "uxtheme": "uxtheme.dll",
    "gdiplus": "gdiplus.dll",
    "version": "version.dll",
    # Graphics / multimedia extension APIs
    "dxgi": "dxgi.dll",
    "d3d11": "d3d11.dll",
    "d3d12": "d3d12.dll",
    "d2d1": "d2d1.dll",
    "dwrite": "dwrite.dll",
    "dsound": "dsound.dll",
    "dinput": "dinput8.dll",
    "xinput": "xinput",           # import lib xinput.lib -> xinput1_4.dll
    "xaudio2": "xaudio2",         # import lib xaudio2.lib -> xaudio2_9.dll
    "d3dcompiler": "d3dcompiler", # import lib d3dcompiler.lib -> d3dcompiler_47.dll
}


def infer_dll(header_path: Path) -> str | None:
    """Infer the DLL name from the header file name."""
    stem = header_path.stem.lower()
    if stem in HEADER_DLL_MAP:
        return HEADER_DLL_MAP[stem]
    # Some headers use a dotted name like winuser.h -> user32.dll.
    for part in stem.replace("-", "_").split("_"):
        if part in HEADER_DLL_MAP:
            return HEADER_DLL_MAP[part]
    return None


def convert_header(header_path: Path, lib_name: str, out_path: Path,
                   include_types: str, emit_types_include: bool,
                   emit_constants: bool, emit_typedefs: bool = False,
                   types_out_path: Path | None = None,
                   emit_macro_functions: bool = True) -> tuple[int, int, int, int, list[str]]:
    """Convert a single header and write the ZV output.

    Returns (function_count, constant_count, macro_function_count, typedef_count, warnings).
    """
    text = header_path.read_text(encoding="utf-8", errors="replace")

    functions, fn_warnings = extract_functions(text)
    constants, const_warnings = ([], [])
    if emit_constants:
        constants, const_warnings = extract_constants(text)

    typedefs: list[tuple[str, str, bool]] = []
    if emit_typedefs:
        typedefs = extract_typedefs(text)

    macro_functions = extract_macro_functions(text) if emit_macro_functions else []

    out_lines = [
        f"// {out_path.name}",
        f"//",
        f"// Auto-generated ZV bindings from {header_path.name}",
        f"// DLL: {lib_name}",
        "//",
        "// Review before use: unsupported parameter types and skipped functions",
        "// are reported below in the generation warnings.",
        "",
    ]

    if emit_types_include:
        out_lines.append(f'#include "{include_types}"')
        out_lines.append("")

    if typedefs:
        types_file = types_out_path or out_path.with_name(out_path.stem + "_types.zv")
        rel_types = types_file.name
        out_lines.append(f'#include "{rel_types}"')
        out_lines.append("")

    if constants:
        out_lines.append(render_constants(constants))
        out_lines.append("")

    if macro_functions:
        out_lines.append(render_macro_functions(macro_functions))
        out_lines.append("")

    out_lines.append(render_extern_block(lib_name, functions))

    out_text = "\n".join(out_lines)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(out_text, encoding="utf-8")

    if typedefs:
        types_file = types_out_path or out_path.with_name(out_path.stem + "_types.zv")
        types_file.parent.mkdir(parents=True, exist_ok=True)
        types_file.write_text(render_typedefs(typedefs) + "\n", encoding="utf-8")

    return len(functions), len(constants), len(macro_functions), len(typedefs), fn_warnings + const_warnings


DEFAULT_SDK_BASE = Path("C:/Program Files (x86)/Windows Kits/10/Include")


# Aggregator for the split kernel32 sub-modules. Written to lib/win/kernel32.zv
# when --all/--auto-sdk is used.
KERNEL32_ZV_CONTENTS = '''// kernel32.zv
//
// Aggregated ZV bindings for the Windows kernel32.dll core API.
// Include this file to pull in all generated kernel32 sub-modules.

#include "types.zv"

#include "kernel32_util.zv"
#include "kernel32_debug.zv"
#include "kernel32_error.zv"
#include "kernel32_handle.zv"
#include "kernel32_synch.zv"
#include "kernel32_sysinfo.zv"
#include "kernel32_thread.zv"
#include "kernel32_file.zv"
#include "kernel32_memory.zv"
#include "kernel32_loader.zv"
#include "kernel32_env.zv"
'''


# Common Windows API types used by all generated bindings. This is written to
# lib/win/types.zv when --all/--auto-sdk is used.
TYPES_ZV_CONTENTS = r'''// types.zv
//
// Common Windows API type aliases for use with the ZV bindings in lib/win.
//
// Where possible these are declared as `newtype` so that distinct Windows
// handle/integer kinds cannot be silently mixed. They have the same ABI as
// their underlying ZV type, so they can be used directly in `extern` blocks.

// --- Integer typedefs --------------------------------------------------------

newtype BYTE   = UINT8;
newtype WORD   = UINT16;
newtype DWORD  = UINT32;
newtype QWORD  = UINT64;

newtype SHORT  = INT16;
newtype USHORT = UINT16;
newtype LONG   = INT32;
newtype ULONG  = UINT32;
newtype BOOL32 = INT32;   // Windows BOOL is a 32-bit int (0 / non-zero)
newtype HRESULT = INT32;  // COM-style status code
newtype SCODE  = HRESULT;

newtype ATOM   = WORD;

newtype SIZE_T  = UINT64; // pointer-sized unsigned (x64)
newtype SSIZE_T = INT64;  // pointer-sized signed (x64)
newtype UINT_PTR = SIZE_T;
newtype INT_PTR  = SSIZE_T;

newtype LONG32  = INT32;
newtype ULONG32 = UINT32;
newtype LONG64  = INT64;
newtype ULONG64 = UINT64;
newtype DWORDLONG = UINT64;

newtype LSTATUS = LONG;
newtype HFILE   = INT32;

newtype ACCESS_MASK = DWORD;
newtype REGSAM      = DWORD;
newtype LANGID      = UINT16;
newtype LCID        = DWORD;
newtype VARTYPE     = UINT16;
newtype OLECHAR     = UINT16;
newtype MMRESULT    = UINT32;

newtype COLORREF = DWORD;

// Pointer-sized message/UI types
newtype LPARAM  = INT_PTR;
newtype WPARAM  = UINT_PTR;
newtype LRESULT = INT_PTR;

// --- Handles ---------------------------------------------------------------

newtype HANDLE = PTR<VOID>;
newtype HINSTANCE = PTR<VOID>;
newtype HMODULE = PTR<VOID>;
newtype HWND = PTR<VOID>;
newtype HDC = PTR<VOID>;
newtype HICON = PTR<VOID>;
newtype HCURSOR = PTR<VOID>;
newtype HBRUSH = PTR<VOID>;
newtype HPEN = PTR<VOID>;
newtype HFONT = PTR<VOID>;
newtype HMENU = PTR<VOID>;
newtype HBITMAP = PTR<VOID>;
newtype HGLOBAL = PTR<VOID>;
newtype HLOCAL = PTR<VOID>;
newtype HKL = PTR<VOID>;
newtype HDESK = PTR<VOID>;
newtype HWINSTA = PTR<VOID>;
newtype HHOOK = PTR<VOID>;
newtype HACCEL = PTR<VOID>;
newtype HMONITOR = PTR<VOID>;
newtype HRGN = PTR<VOID>;
newtype HDWP = PTR<VOID>;
newtype HDEVNOTIFY = PTR<VOID>;
newtype HPOWERNOTIFY = PTR<VOID>;
newtype HTOUCHINPUT = PTR<VOID>;
newtype HSYNTHETICPOINTERDEVICE = PTR<VOID>;
newtype HGESTUREINFO = PTR<VOID>;
newtype HRAWINPUT = PTR<VOID>;
newtype HDROP = PTR<VOID>;
newtype HKEY = PTR<VOID>;

// --- Common pointer typedefs -------------------------------------------------

type LPVOID = PTR<VOID>;
type PVOID  = PTR<VOID>;
type LPCVOID = PTR<VOID>;

type LPBOOL = PTR<BOOL32>;
type PBOOL  = PTR<BOOL32>;
type LPDWORD = PTR<DWORD>;
type PDWORD  = PTR<DWORD>;
type LPHANDLE = PTR<HANDLE>;
type PHANDLE  = PTR<HANDLE>;
type LPWORD = PTR<WORD>;
type PWORD  = PTR<WORD>;
type LPBYTE = PTR<BYTE>;
type PBYTE  = PTR<BYTE>;
type LPINT = PTR<INT32>;
type PINT  = PTR<INT32>;
type LPUINT = PTR<UINT32>;
type PUINT  = PTR<UINT32>;
type PLONG = PTR<LONG>;
type PULONG = PTR<ULONG>;

type LPSECURITY_ATTRIBUTES = PTR<VOID>;
type PSECURITY_ATTRIBUTES = PTR<VOID>;
type LPFILETIME = PTR<VOID>;
type PFILETIME = PTR<VOID>;

type LARGE_INTEGER = PTR<VOID>;
type PLARGE_INTEGER = PTR<VOID>;
type ULARGE_INTEGER = PTR<VOID>;
type PULARGE_INTEGER = PTR<VOID>;

type FARPROC = PTR<VOID>;
type NEARPROC = PTR<VOID>;
type PROC = PTR<VOID>;

type va_list = PTR<VOID>;
type VA_LIST = PTR<VOID>;

// --- String typedefs ---------------------------------------------------------
//
// ZV has two C-interop string primitives: CSTRING (UTF-8, i8*) and WSTRING
// (UTF-16, i16*). The Windows LP*/PC* typedefs map directly to these so that
// `wstr()` and plain CSTRING literals can be passed straight to APIs.

type LPSTR = CSTRING;
type PSTR  = CSTRING;
type LPCSTR = CSTRING;
type PCSTR  = CSTRING;
type LPCH   = CSTRING;
type PCH    = CSTRING;
type LPCCH  = CSTRING;
type PCNZCH = CSTRING;

type LPWSTR = WSTRING;
type PWSTR  = WSTRING;
type LPCWSTR = WSTRING;
type PCWSTR  = WSTRING;
type PCNZWCH = WSTRING;

type LPOLESTR  = WSTRING;
type LPCOLESTR = WSTRING;

// --- OLE/COM/GUID types ------------------------------------------------------

type BSTR = PTR<VOID>;
type REFGUID = PTR<VOID>;
type REFCLSID = PTR<VOID>;
type CLSID = PTR<VOID>;
type IID = PTR<VOID>;
type REFIID = PTR<VOID>;
type CY = PTR<VOID>;

// --- Networking types --------------------------------------------------------

newtype SOCKET = UINT_PTR;
type WSAEVENT = PTR<VOID>;
newtype in_addr = DWORD;

// --- Additional numeric typedefs -------------------------------------------

newtype EXECUTION_STATE = DWORD;
newtype LATENCY_TIME = DWORD;
newtype CONFIGRET = DWORD;
newtype RETURN_TYPE = DWORD;
newtype SECURITY_INFORMATION = DWORD;
newtype DEVINST = DWORD;
newtype DEVNODE = DWORD;
newtype GROUP = DWORD;
newtype FEEDBACK_TYPE = UINT32;
newtype DIALOG_CONTROL_DPI_CHANGE_BEHAVIORS = UINT32;
newtype DIALOG_DPI_CHANGE_BEHAVIORS = UINT32;
newtype DPI_AWARENESS = UINT32;
newtype DPI_HOSTING_BEHAVIOR = UINT32;
newtype ORIENTATION_PREFERENCE = UINT32;
newtype TOOLTIP_DISMISS_FLAGS = UINT32;
newtype MOVESIZE_OPERATION = UINT32;
newtype DEP_SYSTEM_POLICY_TYPE = UINT32;
newtype GET_FILEEX_INFO_LEVELS = UINT32;
newtype FINDEX_INFO_LEVELS = UINT32;
newtype STREAM_INFO_LEVELS = UINT32;
newtype FINDEX_SEARCH_OPS = UINT32;
newtype AUDIT_EVENT_TYPE = UINT32;
newtype UMS_THREAD_INFO_CLASS = UINT32;
newtype READ_DIRECTORY_NOTIFY_INFORMATION_CLASS = UINT32;
newtype FILE_INFO_BY_HANDLE_CLASS = UINT32;
newtype FILE_INFO_BY_NAME_CLASS = UINT32;
newtype DIRECTORY_FLAGS = UINT32;
newtype MEMORY_RESOURCE_NOTIFICATION_TYPE = UINT32;
newtype OFFER_PRIORITY = UINT32;
newtype WIN32_MEMORY_INFORMATION_CLASS = UINT32;
newtype WIN32_MEMORY_PARTITION_INFORMATION_CLASS = UINT32;
newtype WSAESETSERVICEOP = UINT32;
newtype REASON_CONTEXT = PTR<VOID>;
newtype WAITORTIMERCALLBACK = PTR<VOID>;
newtype APPLICATION_RECOVERY_CALLBACK = PTR<VOID>;
newtype BLENDFUNCTION = UINT32;
newtype DEVPROPTYPE = UINT32;
newtype REGDISPOSITION = UINT32;
newtype RESOURCEID = UINT32;
newtype PNP_VETO_TYPE = UINT32;
newtype SHSTOCKICONID = UINT32;

// --- Shell / theme / DWM types ---------------------------------------------

newtype ASSOCF = UINT32;
newtype SHGLOBALCOUNTER = UINT32;
newtype SFBS_FLAGS = UINT32;
newtype STIF_FLAGS = UINT32;
newtype URLIS = UINT32;
newtype SRRF = UINT32;
newtype SHREGDEL_FLAGS = UINT32;
newtype APTTYPE = UINT32;
newtype TA_PROPERTY = UINT32;
newtype THEMESIZE = UINT32;
newtype WINDOWTHEMEATTRIBUTETYPE = UINT32;
newtype DWMTRANSITION_OWNEDWINDOW_TARGET = UINT32;
newtype AgileReferenceOptions = UINT32;
newtype BP_BUFFERFORMAT = UINT32;
newtype SHREGENUM_FLAGS = UINT32;
newtype GESTURE_TYPE = UINT32;
newtype DWM_SHOWCONTACT = UINT32;
newtype ASSOCSTR = UINT32;
newtype ASSOCKEY = UINT32;

// --- Graphics / multimedia types ---------------------------------------------

newtype D2D1_FACTORY_TYPE = UINT32;
type D2D1_POINT_2F = PTR<VOID>;
type D2D1_MATRIX_3X2_F = PTR<VOID>;
newtype D3D_BLOB_PART = UINT32;
newtype D3D_DRIVER_TYPE = UINT32;
newtype D3D_ROOT_SIGNATURE_VERSION = UINT32;
newtype D3D_FEATURE_LEVEL = UINT32;
newtype XAUDIO2_PROCESSOR = UINT32;

type DPI_AWARENESS_CONTEXT = PTR<VOID>;
'''


# Templates for well-known Windows function-like macros. When one of these is
# encountered in a header, it is emitted as a ZV function instead of being
# skipped. The C macro body itself is not parsed; the known ZV implementation
# below is used.
MACRO_FUNCTION_TEMPLATES: dict[str, tuple[str, list[str], str]] = {
    # wingdi.h / windef.h color helpers
    "RGB": ("COLORREF", ["UINT8 r", "UINT8 g", "UINT8 b"],
            "return ((r as UINT32) | ((g as UINT32) << 8) | ((b as UINT32) << 16)) as COLORREF;"),
    "GetRValue": ("UINT8", ["COLORREF rgb"], "return (rgb & 0xFF) as UINT8;"),
    "GetGValue": ("UINT8", ["COLORREF rgb"], "return ((rgb >> 8) & 0xFF) as UINT8;"),
    "GetBValue": ("UINT8", ["COLORREF rgb"], "return ((rgb >> 16) & 0xFF) as UINT8;"),
    # windef.h / winuser.h packing / unpacking helpers
    "MAKEWORD": ("WORD", ["UINT8 a", "UINT8 b"],
                 "return ((a as WORD) | ((b as WORD) << 8)) as WORD;"),
    "MAKELONG": ("LONG", ["UINT16 a", "UINT16 b"],
                 "return ((a as LONG) | ((b as LONG) << 16)) as LONG;"),
    "MAKEWPARAM": ("WPARAM", ["UINT16 a", "UINT16 b"],
                   "return (((a as UINT_PTR) << 16) | (b as UINT_PTR)) as WPARAM;"),
    "MAKELPARAM": ("LPARAM", ["UINT16 a", "UINT16 b"],
                   "return (((a as INT_PTR) << 16) | (b as INT_PTR)) as LPARAM;"),
    "MAKELRESULT": ("LRESULT", ["UINT16 a", "UINT16 b"],
                    "return (((a as INT_PTR) << 16) | (b as INT_PTR)) as LRESULT;"),
    "LOWORD": ("UINT16", ["DWORD_PTR w"], "return (w & 0xFFFF) as UINT16;"),
    "HIWORD": ("UINT16", ["DWORD_PTR w"], "return ((w >> 16) & 0xFFFF) as UINT16;"),
    "LOBYTE": ("UINT8", ["WORD w"], "return (w & 0xFF) as UINT8;"),
    "HIBYTE": ("UINT8", ["WORD w"], "return ((w >> 8) & 0xFF) as UINT8;"),
}


# Curated set of Windows headers that are converted into lib/win/*.zv by --all.
# Each tuple is (SDK-relative header path, output path, include-types path).
CURATED_HEADERS: list[tuple[str, str, str]] = [
    # core kernel32 split headers
    ("um/utilapiset.h", "lib/win/kernel32_util.zv", "types.zv"),
    ("um/debugapi.h", "lib/win/kernel32_debug.zv", "types.zv"),
    ("um/errhandlingapi.h", "lib/win/kernel32_error.zv", "types.zv"),
    ("um/handleapi.h", "lib/win/kernel32_handle.zv", "types.zv"),
    ("um/synchapi.h", "lib/win/kernel32_synch.zv", "types.zv"),
    ("um/sysinfoapi.h", "lib/win/kernel32_sysinfo.zv", "types.zv"),
    ("um/processthreadsapi.h", "lib/win/kernel32_thread.zv", "types.zv"),
    ("um/fileapi.h", "lib/win/kernel32_file.zv", "types.zv"),
    ("um/memoryapi.h", "lib/win/kernel32_memory.zv", "types.zv"),
    ("um/libloaderapi.h", "lib/win/kernel32_loader.zv", "types.zv"),
    ("um/processenv.h", "lib/win/kernel32_env.zv", "types.zv"),
    # core DLLs
    ("um/winuser.h", "lib/win/user32.zv", "types.zv"),
    ("um/shellapi.h", "lib/win/shell32.zv", "types.zv"),
    ("um/wingdi.h", "lib/win/gdi32.zv", "types.zv"),
    ("um/winreg.h", "lib/win/advapi32.zv", "types.zv"),
    ("um/WinSock2.h", "lib/win/ws2_32.zv", "types.zv"),
    ("um/oleauto.h", "lib/win/oleaut32.zv", "types.zv"),
    ("um/combaseapi.h", "lib/win/combase.zv", "types.zv"),
    ("um/cfgmgr32.h", "lib/win/cfgmgr32.zv", "types.zv"),
    ("um/dwmapi.h", "lib/win/dwmapi.zv", "types.zv"),
    ("um/Uxtheme.h", "lib/win/uxtheme.zv", "types.zv"),
    ("um/Shlwapi.h", "lib/win/shlwapi.zv", "types.zv"),
    ("um/winver.h", "lib/win/version.zv", "types.zv"),
    # additional graphics/multimedia APIs
    ("shared/dxgi.h", "lib/win/additional/dxgi.zv", "../types.zv"),
    ("um/d3d11.h", "lib/win/additional/d3d11.zv", "../types.zv"),
    ("um/d3d12.h", "lib/win/additional/d3d12.zv", "../types.zv"),
    ("um/d2d1.h", "lib/win/additional/d2d1.zv", "../types.zv"),
    ("um/dwrite.h", "lib/win/additional/dwrite.zv", "../types.zv"),
    ("um/dsound.h", "lib/win/additional/dsound.zv", "../types.zv"),
    ("um/dinput.h", "lib/win/additional/dinput.zv", "../types.zv"),
    ("um/Xinput.h", "lib/win/additional/xinput.zv", "../types.zv"),
    ("um/xaudio2.h", "lib/win/additional/xaudio2.zv", "../types.zv"),
    ("um/d3dcompiler.h", "lib/win/additional/d3dcompiler.zv", "../types.zv"),
]


def find_windows_sdk_root() -> Path | None:
    """Return the newest Windows 10/11 SDK include directory, or None."""
    base = DEFAULT_SDK_BASE
    if not base.exists():
        return None
    versions = [d for d in base.iterdir() if d.is_dir()]
    if not versions:
        return None
    # Sort by version number (e.g. 10.0.26100.0)
    versions.sort(
        key=lambda p: tuple(int(x) for x in p.name.split(".") if x.isdigit()),
        reverse=True,
    )
    return versions[0]


def main():
    parser = argparse.ArgumentParser(description="Convert Windows SDK C headers to ZV extern bindings")
    parser.add_argument("--header", help="Path to the .h file to convert")
    parser.add_argument("--lib", help="DLL name, e.g. kernel32.dll")
    parser.add_argument("--out", help="Output .zv file")
    parser.add_argument("--input-dir", help="Batch convert every .h file in this directory")
    parser.add_argument("--out-dir", help="Output directory for batch conversion")
    parser.add_argument("--auto-lib", action="store_true", help="Infer the DLL name from the header file name")
    parser.add_argument("--recursive", action="store_true", help="Recurse into subdirectories when using --input-dir")
    parser.add_argument("--include-types", default="types.zv", help='#include path for types.zv (default: "types.zv")')
    parser.add_argument("--constants", action="store_true", help="Also extract numeric #define constants")
    parser.add_argument("--no-types-include", action="store_true", help="Do not emit #include for types.zv")
    parser.add_argument("--extract-typedefs", action="store_true", help="Emit per-header type alias files for simple typedefs")
    parser.add_argument("--all", action="store_true", help="Convert the curated Windows headers into lib/win/ and lib/win/additional/ (also writes types.zv and kernel32.zv)")
    parser.add_argument("--auto-sdk", action="store_true", help="Same as --all (find SDK, infer DLLs, extract typedefs and constants)")
    parser.add_argument("--sdk", help="Windows SDK include directory to use with --all/--auto-sdk")
    args = parser.parse_args()

    if args.all or args.auto_sdk:
        # --all/--auto-sdk is the one-stop switch: infer DLLs, extract typedefs and constants.
        args.auto_lib = True
        args.extract_typedefs = True
        args.constants = True
        args.all = True

        # Always emit the shared type file and the kernel32 aggregator.
        types_path = Path("lib/win/types.zv")
        types_path.parent.mkdir(parents=True, exist_ok=True)
        types_path.write_text(TYPES_ZV_CONTENTS, encoding="utf-8")
        print(f"Wrote shared type aliases to {types_path}")

        kernel32_agg = Path("lib/win/kernel32.zv")
        kernel32_agg.parent.mkdir(parents=True, exist_ok=True)
        kernel32_agg.write_text(KERNEL32_ZV_CONTENTS, encoding="utf-8")
        print(f"Wrote kernel32 aggregator to {kernel32_agg}")

        sdk_root = Path(args.sdk) if args.sdk else find_windows_sdk_root()
        if not sdk_root or not sdk_root.exists():
            parser.error("Could not find Windows SDK include directory; use --sdk")
        total_warnings: list[tuple[str, str]] = []
        processed = 0
        for rel_header, out_rel, inc_types in CURATED_HEADERS:
            header_path = sdk_root / rel_header
            if not header_path.exists():
                print(f"Skipping missing header: {header_path}")
                continue
            out_path = Path(out_rel)
            lib_name = infer_dll(header_path) if args.auto_lib else args.lib
            if not lib_name:
                print(f"Skipping {rel_header}: no DLL mapping (use --auto-lib)")
                continue
            fns, consts, macros, typedefs, warnings = convert_header(
                header_path, lib_name, out_path,
                inc_types, not args.no_types_include, args.constants,
                args.extract_typedefs,
            )
            print(f"{rel_header}: {fns} functions, {consts} constants, {macros} macros, {typedefs} typedefs -> {out_rel}")
            for w in warnings:
                total_warnings.append((rel_header, w))
            processed += 1
        print(f"\nDone. Processed {processed} headers.")
        if total_warnings:
            print(f"Warnings ({len(total_warnings)}):", file=sys.stderr)
            shown = total_warnings[:50]
            for hname, w in shown:
                print(f"  [{hname}] {w}", file=sys.stderr)
            if len(total_warnings) > 50:
                print(f"  ... and {len(total_warnings) - 50} more warnings", file=sys.stderr)
        return

    if args.header:
        if not args.out:
            parser.error("--out is required when converting a single --header")
        lib_name = args.lib
        if not lib_name:
            lib_name = infer_dll(Path(args.header)) if args.auto_lib else None
        if not lib_name:
            parser.error("--lib is required (or use --auto-lib for a known header)")
        fns, consts, macros, typedefs, warnings = convert_header(
            Path(args.header), lib_name, Path(args.out),
            args.include_types, not args.no_types_include, args.constants,
            args.extract_typedefs,
        )
        print(f"Wrote {fns} function(s) to {args.out}")
        if args.constants:
            print(f"Wrote {consts} constant(s)")
        if macros:
            print(f"Wrote {macros} macro function(s)")
        if args.extract_typedefs and typedefs:
            print(f"Wrote {typedefs} type alias(es) to {Path(args.out).with_stem(Path(args.out).stem + '_types')}")
        if warnings:
            print(f"Warnings ({len(warnings)}):", file=sys.stderr)
            for w in warnings:
                print(f"  {w}", file=sys.stderr)
        return

    if args.input_dir:
        if not args.out_dir:
            parser.error("--out-dir is required when using --input-dir")
        input_dir = Path(args.input_dir)
        out_dir = Path(args.out_dir)
        pattern = "**/*.h" if args.recursive else "*.h"
        headers = sorted(input_dir.glob(pattern))
        if not headers:
            print(f"No .h files found in {input_dir}")
            return
        total_fns = total_consts = total_macros = total_typedefs = 0
        all_warnings: list[tuple[str, str]] = []
        unmapped = 0
        for header_path in headers:
            lib_name = args.lib
            if not lib_name:
                lib_name = infer_dll(header_path) if args.auto_lib else None
            if not lib_name:
                unmapped += 1
                continue
            rel = header_path.relative_to(input_dir)
            out_path = out_dir / rel.with_suffix(".zv")
            types_out_path = out_path.with_name(out_path.stem + "_types.zv") if args.extract_typedefs else None
            fns, consts, macros, typedefs, warnings = convert_header(
                header_path, lib_name, out_path,
                args.include_types, not args.no_types_include, args.constants,
                args.extract_typedefs, types_out_path,
            )
            total_fns += fns
            total_consts += consts
            total_macros += macros
            total_typedefs += typedefs
            for w in warnings:
                all_warnings.append((header_path.name, w))
        mapped_count = len(headers) - unmapped
        print(f"Converted {mapped_count} header(s) to {out_dir}")
        if unmapped:
            print(f"Skipped {unmapped} header(s) with no DLL mapping")
        print(f"Total functions: {total_fns}, constants: {total_consts}, macro functions: {total_macros}", end="")
        if args.extract_typedefs:
            print(f", type aliases: {total_typedefs}")
        else:
            print()
        if all_warnings:
            print(f"Warnings ({len(all_warnings)}):", file=sys.stderr)
            for hname, w in all_warnings:
                print(f"  [{hname}] {w}", file=sys.stderr)
        return

    parser.error("Either --header, --input-dir, or --all is required")


if __name__ == "__main__":
    main()
