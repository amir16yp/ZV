<p align="center">
  <img src="icons/icon-transparent-256.png" alt="ZV Logo" width="128" height="128">
</p>

# ZV Programming Language

ZV is a small, statically typed systems language I designed that compiles to
LLVM IR. It is intentionally opinionated: I wanted a language that gives me
explicit control over memory, resources, and hardware, keeps the syntax close
to C, and treats native interop and bare-metal kernels as first-class
concerns rather than afterthoughts.

The compiler is written in C# (.NET 10) using LLVMSharp, and it supports three
distinct targets:

* **Hosted executable** (`exe`) — compiled with Clang into a normal Windows or
Linux process.
* **Shared library** (`lib`) — compiled with Clang into a Windows DLL or Linux
shared object, exposing only functions marked `export`.
* **Freestanding x86 kernel** (`os-x86`) — a tech-demo target that compiles
into a Multiboot-compatible ELF kernel bootable with QEMU or GRUB. It exists
mostly because I wanted to prove the same frontend could go all the way down to
bare metal, not because it's a production kernel toolchain.

---

## Table of Contents

* [Philosophy](#philosophy)
* [Quick Start](#quick-start)
* [Project Layout](#project-layout)
* [CLI Usage](#cli-usage)
* [Language Syntax](#language-syntax)
* [Comments and Literals](#comments-and-literals)
* [Types](#types)
* [Variables](#variables)
* [Functions](#functions)
* [Control Flow](#control-flow)
* [Structs and Arrays](#structs-and-arrays)
* [Casts](#casts)
* [Extern Bindings](#extern-bindings)
* [Directives](#directives)
* [#include Directives](#include-directives)
* [Built-in Functions](#built-in-functions)
* [Hosted / General](#hosted--general)
* [Threads and Concurrency](#threads-and-concurrency)
* [Terminal UI (curses)](#terminal-ui-curses)
* [Bare Metal / Kernel](#bare-metal--kernel)
* [Exception Handling](#exception-handling)
* [Processes: respawn()](#processes-respawn)
* [Safety: bounds checking and unsafe](#safety-bounds-checking-and-unsafe)
* [Ownership: move and copy](#ownership-move-and-copy)
* [Type Aliases](#type-aliases)
* [Compilation Targets](#compilation-targets)
* [Examples](#examples)
* [Standard Library Helpers](#standard-library-helpers)
* [Development](#development)
* [Reserved / Not Yet Implemented](#reserved--not-yet-implemented)

---

## Contributing

Bug reports, feature requests, and pull requests are welcome. Open an issue or
start a discussion on the [GitHub repository](https://github.com/amir16yp/ZV) —
there are no special requirements or CLAs, just keep things constructive.

---

## Philosophy

ZV is C minus the bullshit.

I wanted raw performance, fast execution time, and direct access to the hardware without the years of legacy bullshit that come with it: no C header bureaucracy, no implicit casts, no `int` booleans, no dangling pointers that, when chased down manually to track down bugs, only prove that nothing should be said to imply that a programming language from 1972 doesn't need its variables bound-checked. Memory and resource allocation are included in the code; the compiler will warn me if I do anything dumb, and the generated LLVM IR code still resembles my original source code enough for me to figure out what the machine is really doing.

I have no inclination to type up something resembling a parsing test for the syntax of modern systems programming languages, even those with followers like Rust and Zig. C syntax is unremarkable, and that is precisely the point.

Opening a file and handling the error in Rust is

```rust
let f: Result<File, std::io::Error> = File::open("missing.txt");
```

in ZV it's just

```zv
try {
    PTR<VOID> f = fopen("missing.txt", "r");
} catch (e) {
    print("Caught: %s", e.message);
}
```

and a heap-allocated array of ints in C++ is

```cpp
std::vector<int> nums = {1, 2, 3, 4};
```

versus ZV's

```zv
INT32[] nums = [1, 2, 3, 4];
```

Neither of those Rust/C++ snippets is exotic, they're both ordinary code you'd
write ten times a day, but the `Result<T, E>` and `std::vector<T>` wrapping is
still there every single time.

Calling into a native library is the same story. Rust needs an `unsafe`
block, an `extern "C"` binding, and a manual `CString` conversion just to
call `MessageBoxA`:

```rust
use std::ffi::CString;

#[link(name = "user32")]
extern "C" {
    fn MessageBoxA(hwnd: *mut c_void, text: *const i8, caption: *const i8, utype: u32) -> i32;
}

let text = CString::new("hi").unwrap();
let caption = CString::new("ZV").unwrap();
unsafe { MessageBoxA(std::ptr::null_mut(), text.as_ptr(), caption.as_ptr(), 0) };
```

ZV's `extern` block reads like the Win32 header it's binding, and string
conversion is a single `cstr()` call with no `unsafe`:

```zv
extern "user32.dll" {
    INT32 MessageBoxA(PTR<VOID> hwnd, CSTRING text, CSTRING caption, UINT32 type_val);
}

MessageBoxA(0, cstr("hi"), cstr("ZV"), 0);
```

Native interop and bare-metal kernels are first-class concerns in ZV rather
than afterthoughts bolted on with `unsafe` and FFI crates.

Propagating an error through a few calls is another everyday case. C makes
you check a return value or `errno` after every call and bubble it up by
hand:

```c
FILE *f = fopen(path, "r");
if (!f) {
    fprintf(stderr, "open failed: %s\n", strerror(errno));
    return -1;
}
if (fseek(f, 0, SEEK_END) != 0) {
    fprintf(stderr, "seek failed: %s\n", strerror(errno));
    fclose(f);
    return -1;
}
```

Rust replaces the manual checks with `?`, but every function in the chain
now has to return `Result<T, E>` and thread the error type through its
signature:

```rust
fn read_size(path: &str) -> Result<u64, std::io::Error> {
    let mut f = File::open(path)?;
    f.seek(SeekFrom::End(0))?;
    Ok(f.stream_position()?)
}
```

ZV lets the calls fail naturally and catches the failure once, wherever it's
convenient, without changing every function signature along the way:

```zv
try {
    PTR<VOID> f = fopen(path, "r");
    fseek(f, 0, 2);
    INT64 size = ftell(f);
} catch (e) {
    print("Failed: %s", e.message);
}
```

The memory bugs that make C miserable to debug are the same handful every
time, so ZV catches them structurally instead of relying on the programmer's
memory of where every buffer ends.

An out-of-bounds write in C just corrupts whatever's next to the array,
silently:

```c
int a[4];
a[10] = 5;   // undefined behavior, no error, corrupts nearby memory
```

ZV bounds-checks array access by default, so the same mistake is a compile
error for a constant index and a catchable exception for a variable one:

```zv
INT32[4] a;
a[10] = 5;          // compile error: index 10 out of bounds for length 4

INT32[] nums = INT32[10];
INT32 i = 100;
nums[i] = 1;        // runtime IndexOutOfBoundsException
```

Use-after-free is another classic: C happily lets you keep using a pointer
after `free()`, and the bug only shows up later as a crash or corrupted data:

```c
int *nums = malloc(10 * sizeof(int));
free(nums);
nums[0] = 1;   // undefined behavior, no diagnostic
```

ZV tracks whether a variable has been freed or moved and refuses to compile
if it's used afterward:

```zv
INT32[] nums = INT32[10];
free(nums);
nums[0] = 1;   // compile error: 'nums' was already freed
free(nums);    // compile error: 'nums' was already freed
```

Because ZV frees owned heap allocations automatically at the end of their
scope, most code never has to call `free()` at all, which removes the double
free and use-after-free mistakes before they can be written, not just after.

But, again, I am no masochist. ZV includes things that, for reasons of convenience, I happen to want: built-in `try`/`catch`/`throw` exceptions so that I do not have to write every function in error-code chains, `newtype` for confusing units without paying the cost of a runtime wrapper, and cross-platform builtins such as `print`, `len`, `cstr`, `get_timestamp`, file I/O, threads, and mutexes—so that I do not have to repeatedly link and wrap libc/pthreads/Win32 by hand.


---

## Quick Start

### Install

Pre-built binaries are available on the [Releases](https://github.com/amir16yp/ZV/releases)
page. You do not need the .NET SDK to use a release build; only Clang is required
to link executables and libraries.

#### Windows (MSI installer)

Two x64 MSI installers are provided:

* `ZV-Setup-x64.msi` — installs for the current user only, into
  `%LOCALAPPDATA%\ZV`, and adds it to your user `PATH`. Does **not** require
  admin.
* `ZV-Setup-x64-AllUsers.msi` — installs for all users, into
  `C:\Program Files (x86)\ZV`, and adds it to the system `PATH`. Requires
  admin.

Both MSIs are framework-dependent x64 packages and require the [.NET 10
Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) to
already be installed. During setup you can opt in to installing the optional
build tools: Scoop plus LLVM (which provides `clang` and `lld`). These tools
are required if you want to compile ZV executables and DLLs.

Download the MSI you want and run it. Then open a new terminal and run:

```powershell
zv checkdeps
```

#### Windows (portable zip)

Download `ZV-win-x64.zip`, extract it, and run the included installer:

```powershell
.\setup\install.bat
```

This copies `ZV.exe` and the standard `lib/` folder into `%LOCALAPPDATA%\ZV`
and updates your user `PATH`. To uninstall, delete that folder and remove it
from `PATH`.

#### Linux

Download `ZV-linux-x64.tar.gz`, extract it, and run:

```bash
./setup/install.sh
```

This installs the `zv` binary to `~/.local/bin` and the standard library to
`~/.local/share/zv/lib`, then ensures `~/.local/bin` is on your `PATH`. To
uninstall, remove those paths.

Arch Linux users can use the provided AUR PKGBUILDs in `packaging/aur/`
instead:

* `zv-bin` installs the latest release tarball (`depends: clang`, `lld`).
* `zv-git` builds from source (`makedepends: dotnet-sdk`, `git`; `depends: clang`,
  `lld`).

The POSIX install script will also work on macOS if you build from source.

#### Standard library includes

Once installed, headers from the shipped `lib/` folder can be included with
angle brackets:

```zv
#include <lib/prng.zv>
```

Local project files still use quotes:

```zv
#include "common.zv"
```

You can add extra system include directories with the `ZV_INCLUDE_PATH`
environment variable, using the platform path separator (`;` on Windows, `:` on
Unix).

### Prerequisites

* [Clang](https://clang.llvm.org/) and `ld.lld` in your `PATH` (needed for
`exe`, `lib`, and `os-x86` linking)
* Optional: [QEMU](https://www.qemu.org/) (to boot `-target os-x86` kernels)

To build the compiler itself from source you also need the
[.NET 10 SDK](https://dotnet.microsoft.com/).

Once installed, run `ZV checkdeps` (or `dotnet run -- checkdeps` from source) to
verify every tool the compiler needs is discoverable on `PATH`. See
[Checking Toolchain Dependencies](#checking-toolchain-dependencies).

### Build and Test

```bash
dotnet build
dotnet test
```

### Compile a ZV Program

```bash
# Compile a single file to LLVM IR
dotnet run -- hello.zv

# Compile and link a hosted executable
dotnet run -- hello.zv -o hello.exe

# Compile and link a shared library (.dll on Windows, .so on Linux)
dotnet run -- mylib.zv -target lib -o mylib.dll

# Build a bootable x86 kernel and launch it in QEMU
dotnet run -- kernel.zv -target os-x86 -o kernel.elf -run
```

---

## Project Layout

```text
ZV/
├── Program.cs                    # CLI driver and target linkers
├── Compiler/
│   ├── Lexer/                    # Tokenizer
│   ├── Parser/                   # Recursive-descent parser
│   ├── AST/                      # AST node definitions
│   ├── Backend/                  # LLVM IR generator and builtins
│   │   ├── LlvmGenerator.cs
│   │   ├── LlvmGenerator.Builtins.cs
│   │   ├── LlvmGenerator.Curses.cs
│   │   ├── LlvmGenerator.Cpu.cs
│   │   ├── LlvmGenerator.Framebuffer.cs
│   │   ├── LlvmGenerator.Freestanding.cs
│   │   ├── LlvmGenerator.Ps2.cs
│   │   ├── LlvmGenerator.Serial.cs
│   │   └── LlvmGenerator.Vga.cs
│   └── Tests/                    # xUnit parser and backend tests
└── .github/workflows/            # CI build and release
```

---

## CLI Usage

```text
ZV <file or directory> [-o output] [-target exe|lib|os-x86] [-L libdir]... [-run] [-O|--optimize] [-copt O0|O1|O2|O3|Os|Oz|list] [-v|--verbose]
ZV checkdeps
```

| Flag | Description |
|------|-------------|
| `-o` | Output path. `.exe`/no extension forces a linked executable. |
| `-target exe` | Default. Hosted application linked with Clang. |
| `-target lib` | Shared library (`.dll`/`.so`). Only `export`ed functions are visible. |
| `-target os-x86` | Freestanding x86 kernel. |
| `-L <dir>` | Add a directory to the linker's library search path. Repeatable. |
| `-run` | After building an `os-x86` kernel, launch it in QEMU. |
| `-O`, `--optimize` | Run LLVM's in-process optimization pipeline (mem2reg, instcombine, simplifycfg, reassociate, gvn) before emitting. Opt-in; off by default. |
| `-copt <level>` | Optimization level passed to clang as `-O<level>` when linking (`O0`, `O1`, `O2`, `O3`, `Os`, `Oz`). Defaults to `O2`. Use `-copt list` to print the available levels. |
| `-v`, `--verbose` | Print each compiler stage (lexing/parsing per file, codegen, optimization passes, emission, linking) with timing, prefixed `[verbose]`. |

When a directory is passed, the compiler recursively scans for `.zv` files and
compiles them as a single module. `#include` is also supported inside a file.

### Checking Toolchain Dependencies

```bash
ZV checkdeps
```

Scans `PATH` for the external tools the compiler shells out to and reports which
ones are available, without invoking any of them:

| Tool | Required | Used for |
|------|----------|----------|
| `clang` | Yes | Compiling/linking `exe`, `lib`, and `os-x86` targets. |
| `ld.lld` | No | Linking freestanding `os-x86` kernels. |
| `llvm-readobj` | No | Reading a DLL's export table (for `extern "path/to.dll"`). |
| `llvm-dlltool` | No | Generating a Windows import library from a DLL's exports. |
| `qemu-system-i386` | No | Booting `os-x86` kernels with `-run`. |

Exits with a non-zero status if a required tool is missing.

---

## Language Syntax

ZV source files use the `.zv` extension. The grammar is intentionally small and
close to C because I wanted the language to be immediately readable to anyone
who knows C, without piling on the syntax extensions that make other systems
languages feel like a different language every six months.

### Comments and Literals

```zv
// Line comment
/* Block comment */

42          // INT32 literal
1_000_000   // digit separators are allowed in numeric literals
0xFF_FF     // hex literals may also use underscores
3.14        // FLOAT64 literal
1_000.000_001 // underscores work in floats too
"hello"     // STRING literal
'A'         // CHAR literal
true false  // BOOL literals
null        // null pointer value
```

Underscores are ignored when the literal is parsed, so `1_000_000` is exactly
`1000000`.

String escape sequences: `\n`, `\t`, `\r`, `\\`, `\"`, `\0`.

### Types

Primitive types are case-insensitive:

| Category | Types |
|----------|-------|
| Signed integers | `INT8`, `INT16`, `INT32`, `INT64`, `INT128` |
| Unsigned integers | `UINT8`, `UINT16`, `UINT32`, `UINT64`, `UINT128` |
| Floating point | `FLOAT32`, `FLOAT64` |
| Other | `BOOL`, `CHAR`, `VOID` |
| String | `STRING` (UTF-8 bytes, immutable, length-aware: `{ i8*, i64 }`) |
| C string | `CSTRING` (NUL-terminated `i8*`) |
| Wide string | `WSTRING` (NUL-terminated UTF-16 `i16*`) |
| Typed pointer | `PTR<T>` (pointer to `T`; `PTR<VOID>` is an opaque `i8*`) |
| Dynamic arrays | `INT32[]`, `CSTRING[]`, etc. (fat pointer: `{ T*, i64 }`) |
| Fixed-size arrays | `INT32[64]`, etc. (`[64 x T]` stack value) |
| User-defined | `struct Point { ... }` |

`STRING` is a length-aware UTF-8 value (`{ i8*, i64 }`) that does not assume a
NUL terminator; `CSTRING` is a plain NUL-terminated `i8*` used for C interop;
`WSTRING` is a NUL-terminated UTF-16 `i16*` used for Windows wide-character APIs.
Dynamic arrays (`T[]`) are fat pointers to heap-allocated memory. Fixed-size
arrays (`T[N]`) are stack-resident LLVM array values (`[N x T]`).

### Variables

```zv
INT32 x = 10;
UINT64 big;
FLOAT32 pi = 3.14;
BOOL enabled = true;
STRING name = "ZV";
CSTRING ptr = cstr(name);
WSTRING wptr = wstr(name);
CHAR c = 'A';
CONST INT32 MAX = 100;   // Constant, requires initializer (lowercase `const` also works)

INT32[] nums = [1, 2, 3];

INT32[64] stackArr;           // fixed-size stack array, zero-initialized
INT32[64] filled = 5;         // all elements = 5
INT32[4] explicit = [1, 2, 3, 4];
INT32[] heapArr = INT32[64];  // heap array, zero-initialized
INT32[] heapFilled = INT32[64](7);
```

Global variables are emitted as LLVM globals; local variables live on the stack
(`alloca`).

### Functions

```zv
INT32 add(INT32 a, INT32 b) {
    return a + b;
}

@entry
UINT32 main(CSTRING[] args) {
    print("Hello, world!");
    return 0;
}
```

`@entry` marks the program entry point. It should accept `CSTRING[] args` and
return an integer. For `os-x86`, the compiler emits a Multiboot `_start` stub
that calls this function.

```zv
export INT32 add(INT32 a, INT32 b) {
    return a + b;
}
```

`export` marks a function as part of the public ABI of a `-target lib` build. It
has no effect for `exe`/`os-x86` targets. See [Compilation Targets](#compilation-targets).

### Control Flow

```zv
if (x > 0) {
    print("positive");
} else {
    print("non-positive");
}

while (x < 10) {
    x = x + 1;
}

for (INT32 i = 0; i < 10; i = i + 1) {
    print(i);
}

break;
continue;
return;
return x;
```

### Operators

| Precedence | Operators | Description |
|------------|-----------|-------------|
| Highest | `++`, `--` (postfix) | Postfix increment/decrement |
| | `++`, `--` (prefix), `-`, `!`, `~` | Prefix increment/decrement, unary minus, logical not, bitwise NOT |
| | `as` | Type cast |
| | `*`, `/`, `%` | Multiplication, division, modulo |
| | `+`, `-` | Addition, subtraction |
| | `<`, `<=`, `>`, `>=` | Comparisons |
| | `==`, `!=` | Equality |
| | `&` | Bitwise AND |
| | `^` | Bitwise XOR |
| | <code>&#124;</code> | Bitwise OR |
| | `<<`, `>>` | Bitwise shift left, shift right (logical, not sign-extending) |
| | `&&` | Logical AND (short-circuiting) |
| | <code>&#124;&#124;</code> | Logical OR (short-circuiting) |
| | `?:` | Ternary conditional |
| Lowest | `=`, `+=`, `-=`, `*=`, `/=` | Assignment and compound assignment |

```zv
INT32 a = 5;
a++;              // postfix: returns old value, increments a
++a;              // prefix: increments a, returns new value

UINT32 flags = 0xFF;
UINT32 inverted = ~flags;         // bitwise NOT

BOOL ok = (x > 0) && (y < 100); // short-circuiting
INT32 sign = (x < 0) ? -1 : 1;  // ternary
INT32 mask = flags & 0x0F;        // bitwise AND
INT32 bits = flags | 0x80;        // bitwise OR
INT32 toggle = flags ^ 0x01;      // bitwise XOR
UINT32 shifted = flags << 3;      // shift left
UINT32 unshifted = flags >> 3;    // shift right (logical: zero-filled, regardless of signedness)

total += value;                 // compound assignment: total = total + value
i -= 1;
scale *= 2;
count /= 4;
```

`&&` and `||` are short-circuiting: the right-hand side is only evaluated if
needed. The ternary operator `?:` also only evaluates the branch that is taken.
Compound assignments (`+=`, `-=`, `*=`, `/=`) desugar to a regular assignment
with the corresponding arithmetic operator.

### Structs and Arrays

```zv
struct Point {
    INT32 x;
    INT32 y;
}

packed struct Compact {
    INT8 a;
    INT8 b;
}

VOID demo() {
    Point p;
    p.x = 10;
    p.y = 20;
    print(p.x + p.y);

    INT32[] values = [1, 2, 3, 4];
    values[0] = 100;
    print(len(values));   // returns INT64
}
```

#### Struct literals

A struct value can be built with a named-field literal, either with an explicit
type name or, when the target type is already known from context (a variable
or field declaration), in a shorter bare-brace form:

```zv
struct Vec2 {
    FLOAT32 x;
    FLOAT32 y;
}

struct Sprite {
    CSTRING name;
    Vec2 position;
    Vec2 scale;
}

// Explicitly typed - can be used anywhere (call arguments, return values, ...)
// since the literal carries its own type.
Sprite a = Sprite {
    name = cstr("player"),
    position = Vec2 { x = 320.0, y = 240.0 },
    scale = Vec2 { x = 1.0, y = 1.0 }
};

// Bare-brace form - the type is inferred from the declared type of the
// variable/field being initialized, so it can be omitted, including for
// nested fields.
Sprite b = {
    name = cstr("enemy"),
    position = { x = 0.0, y = 0.0 },
    scale = { x = 1.0, y = 1.0 }
};
```

Fields not mentioned in a literal are left zero-initialized (see
[Ownership: move and copy](#ownership-move-and-copy) for why this matters for
struct fields that own heap memory).

#### Arrays

ZV has two deliberately different array kinds. That is an intentional design
choice: stack arrays and heap arrays have different lifetimes, performance
characteristics, and ownership rules, and I want the type system to make that
distinction visible instead of hiding it behind a single abstraction.

**Dynamic arrays: `T[]`**

A fat pointer `{ T*, i64 }` to heap-allocated memory. The programmer owns the
allocation. When an owning variable goes out of scope, the compiler inserts a
matching `free()` automatically — this is deterministic scope-based cleanup,
not garbage collection. Explicit `free(x)` is still allowed for early release
and for values that outlive their declaring scope.

```zv
INT32[] nums = [1, 2, 3, 4];
INT32[] zeros = INT32[64];          // 64 zeroed heap elements
INT32[] sevens = INT32[64](7);      // 64 heap elements filled with 7

print(len(nums));   // INT64 length
// No explicit free needed here — 'nums', 'zeros', and 'sevens' are freed
// automatically when the block ends.
```

Dynamic arrays can be returned from functions; ownership is transferred to the
caller, so the local variable is not freed. `move()` makes the transfer explicit:

```zv
INT32[] create_numbers() {
    INT32[] numbers = INT32[64];
    return numbers;          // ownership returned to caller
}

INT32[] create_more() {
    INT32[] more = INT32[64];
    return move(more);       // explicit transfer, same effect
}
```

**Fixed-size arrays: `T[N]`**

A value type that lives in the current stack frame. It is represented in LLVM
as `[N x T]`. No `free()` is needed — the storage disappears when the scope ends.

```zv
INT32[64] numbers;                  // zero-initialized
INT32[64] values = 5;               // all elements = 5
INT32[4] explicit = [1, 2, 3, 4];   // exact count required

numbers[0] = 42;
```

Returning a fixed-size array from a function is an error because it would
return a pointer to dead stack space.

#### Multidimensional arrays

Fixed-size arrays nest: each bracket adds an inner dimension, so `T[W][H]` is
an array of `H` arrays of `W` elements. For example, `INT32[3][2]` is a 2-row by
3-column matrix (stored as `[2 x [3 x i32]]`).

```zv
INT32[3][2] matrix;                 // zero-initialized 2x3 matrix
INT32[3][2] matrix = [[1, 2, 3],
                     [4, 5, 6]];   // explicit nested initializer
INT32[3][2] filled = 7;             // every scalar element set to 7
INT32[3][2] partial = [[1, 2],      // missing slots are zero-filled
                       [3]];

matrix[0][1] = 10;                  // row 0, column 1
matrix[1][2] = matrix[0][0] + 5;
```

Whole fixed-size arrays can be copied by assignment, passed to functions, and
queried with `len()`. Rows can be sliced and passed by reference.

```zv
VOID sumRow(INT32[3] row) { }

INT32[3][2] a = [[1, 2, 3], [4, 5, 6]];
INT32[3][2] b = a;                  // by-value copy
sumRow(a[0]);                        // pass first row by reference

print(len(a));                      // outer dimension: 2
print(len(a[0]));                   // inner dimension: 3
```

`array_copy()` works with fixed-size arrays as well, copying raw elements in a
single block:

```zv
INT32[3] src = [1, 2, 3];
INT32[3] dst;
array_copy(dst, src);

INT32[3][2] m1 = [[1, 2, 3], [4, 5, 6]];
INT32[3][2] m2;
array_copy(m2, m1);
```

A contiguous heap-allocated matrix can be built as a dynamic array of fixed-size
rows. Memory is one flat allocation; `array_copy()` can fill individual rows:

```zv
INT32[3][] rows = INT32[3][2];      // 2 rows of 3 columns, contiguous
rows[0][1] = 42;
array_copy(rows[1], [7, 8, 9]);
print(len(rows));                   // 2
print(len(rows[0]));                // 3
```

Dynamic arrays can also be jagged, where each row is its own allocation:

```zv
INT32[][] grid = [[1, 2, 3],
                 [4, 5, 6]];

grid[0][1] = 99;                    // row 0, element 1
print(len(grid));                   // rows: 2
print(len(grid[0]));                // length of row 0: 3
```

All forms are bounds-checked: an out-of-range index produces
`IndexOutOfBoundsException` at runtime, or a compile error if the index is a
provably out-of-bounds constant.

### Casts

Casts use the `as` keyword:

```zv
CSTRING ptr = cstr(name);
STRING s = ptr as STRING;
INT32 small = big as INT32;
BOOL flag = x as BOOL;
```

Supported conversions:

* Integer ↔ integer of a different width (truncation or sign-extension)
* Integer ↔ floating point (`FLOAT32`/`FLOAT64`)
* Floating point ↔ floating point of a different width
* Pointer ↔ pointer (bitcast)
* Integer ↔ pointer (only inside `unsafe { ... }`)
* Any integer, float, or pointer → `BOOL` (tests for non-zero / non-null)
* `BOOL` → integer or floating point (`true` becomes `1`, `false` becomes `0`)
* Dynamic array (`T[]`) → element pointer
* Dynamic array (`T[]`) → dynamic array with a different element pointer type
* `STRING` → `CSTRING` (raw reinterpretation: extracts the data pointer
  without copying or NUL-terminating it; prefer `cstr(s)`, which makes a
  safe, NUL-terminated copy, unless you know the `STRING`'s buffer is
  already NUL-terminated)
* `CSTRING` → `STRING` (measures length with `strlen`)

Unsupported or nonsensical combinations (for example, casting a `struct` to an
integer or an array value to a float) produce a compile-time error with a clear
message describing the source and target types.

### Extern Bindings

```zv
extern "user32.dll" {
    INT32 MessageBoxA(PTR<VOID> hwnd, CSTRING text, CSTRING caption, UINT32 type_val);
    INT32 msg_box(PTR<VOID> hwnd, CSTRING text, CSTRING caption, UINT32 type_val) = "MessageBoxA";
}
```

A bare library name (no `/` or `\`) is forwarded to the linker as `-l<libname>`
(minus any `.dll`/`.lib`/`.so` suffix) and resolved through the linker's default
search paths and any directories added with `-L`. The optional `= "native_symbol"`
clause maps a ZV name to a different C symbol.

#### Linking a non-standard library

If the library name contains a path separator, it's treated as a path to a
concrete file and passed straight to the linker instead of `-l<name>` — this is
how you link against a DLL/`.so` that isn't installed anywhere on the system's
default search path:

```zv
extern "./vendor/mylib.dll" {
    INT32 my_add(INT32 a, INT32 b);
}
```

Notes:

* On Linux, a path to a `.so` links directly, since ELF shared objects carry
their own symbol table that the linker can read.
* On Windows, `lld-link` cannot link directly against a `.dll` — it needs the
companion **import library** (`.lib`) that's normally produced alongside a DLL
when it's built. If a `.lib` with the same name sits next to the `.dll`, it's
used automatically. Otherwise, the compiler generates one on the fly from the
DLL's export table (via `llvm-readobj --coff-exports` + `llvm-dlltool`, both of
which ship with LLVM/Clang) and caches it as `<dll>.generated.lib` next to the
DLL, regenerating it only if the DLL changes.
* You can also point directly at an existing `.lib` (`extern "./vendor/mylib.lib"`)
to skip export-table generation entirely.

### Directives

```zv
#include "common.zv"       // textual include with cycle detection
#define BUFFER_SIZE 1024    // simple macro replacement

@entry
UINT32 my_main(CSTRING[] args) {
    return 0;
}
```

* **`#include "path"`** — Textually includes another `.zv` file at this location.
  Includes are tracked recursively and cyclic includes are ignored to prevent
  infinite expansion.
* **`#define NAME value`** — Simple textual macro replacement. Any identifier that
  matches `NAME` is replaced by `value` before parsing. There is no parameterization
  or conditional compilation; it is a straightforward token substitution.

### #include Directives

`#include` is textual: the contents of the included file are expanded in place,
exactly like C/C++. ZV supports two forms that differ in how the path is
resolved.

**Local includes** use double quotes:

```zv
#include "common.zv"
```

The compiler searches relative to the directory containing the file that
contains the `#include`, then relative to the current working directory. Use
this for project-local headers.

**System includes** use angle brackets:

```zv
#include <lib/prng.zv>
```

The compiler searches the configured system include directories. By default
this includes:

* A `lib/` folder next to the `zv` binary (portable install).
* The per-user install location (`%LOCALAPPDATA%\ZV` on Windows,
  `~/.local/share/zv` on Linux).
* Common system-wide locations on Linux (`/usr/lib/zv`, `/usr/share/zv`,
  `/usr/local/lib/zv`, `/usr/local/share/zv`).
* Any directory listed in the `ZV_INCLUDE_PATH` environment variable, using the
  platform path separator (`;` on Windows, `:` on Linux).

So after a normal install, `#include <lib/prng.zv>` finds the shipped
standard library without any extra flags.

You can also jump from an `#include` to its target file in the language server
(Ctrl+Click / Go to Definition).

### Attributes

Attributes use the `@` prefix and appear before the declaration they modify:

* **`@entry`** — Marks the following function as the program entry point. It must
  accept `CSTRING[] args` and return an integer type. For `-target os-x86`, the
  compiler emits a Multiboot `_start` stub that calls it.
* **`@export`** — Marks a function as part of the public ABI (same as `export`
  keyword).
* **`@packed`** — Marks a struct with no padding between fields (same as `packed`
  keyword).

---

## Built-in Functions

Built-ins are recognized by name and do not need an `extern` declaration.

### Hosted / General

| Function | Description |
|----------|-------------|
| `print(...)` | Print to stdout. If the first argument is a string literal it is used as a `printf` format; otherwise a format is inferred from the argument types. `STRING` values print as length-delimited UTF-8. |
| `len(s)` | Returns the length of a `STRING` or dynamic array (`INT64`). |
| `cstr(s)` | Converts a `STRING` to a `CSTRING` by allocating a fresh, NUL-terminated heap copy of its bytes (a no-op passthrough if `s` is already a `CSTRING`). Bound directly to a variable it is owned and freed at end of scope; used inline it is freed automatically after the enclosing statement. See [Strings](#strings). |
| `wstr(s)` | Converts a `STRING` or `CSTRING` to a `WSTRING` by allocating a fresh, NUL-terminated UTF-16 copy using `MultiByteToWideChar(CP_UTF8)`. Passing an existing `WSTRING` returns it unchanged. Lifetime is handled the same as `cstr()`. Currently only supported on Windows. |
| `array_copy(dest, src)` | Copies all of `src` into the start of `dest` (both dynamic arrays of the same element type). Uses `memmove`, so aliased/overlapping arrays copy correctly. Throws `ArrayCopyException` at runtime if `src` is longer than `dest`. |
| `array_copy(dest, dest_offset, src, src_offset, count)` | Copies `count` elements starting at `src_offset` in `src` into `dest` starting at `dest_offset`. Throws `ArrayCopyException` at runtime if either range is out of bounds. In both forms, mismatched element types, negative offsets/counts, or an out-of-range access that's provable from array literals at the call site are rejected at compile time. |
| `alloc(INT64 size)` | Allocate memory with `malloc` (`PTR<VOID>`). Throws `OutOfMemoryException` if allocation fails. |
| `free(value, ...)` | Free heap memory early. Accepts a pointer, a `STRING`, or a dynamic array (fat pointer); extracts the data pointer automatically. Owned dynamic arrays are normally freed automatically at the end of their scope, so `free()` is only needed for early release or non-owning pointers. |
| `copy(x)` | Returns a bitwise copy of `x`; only valid for non-owning types. See [Ownership](#ownership-move-and-copy). |
| `move(x)` | Returns the value of `x` and invalidates the source variable (ownership transfer). See [Ownership](#ownership-move-and-copy). |
| `get_timestamp()` | Unix epoch seconds (`INT64`). |
| `get_timestamp_ms()` | Unix epoch milliseconds (`INT64`). |
| `Exception(STRING message)` | Constructs an `Exception` value carrying `message`. See [Exception Handling](#exception-handling). |
| `respawn()` | Relaunches this program as a new process; returns a `PROCESS`. See [Processes: respawn()](#processes-respawn). |
| `exit(INT32 code)` | Terminates the current process immediately with `code` (`libc exit()`). |

**File I/O** (thin wrappers around C stdio):

| Function | Description |
|----------|-------------|
| `fopen(path, mode)` | Opens a file (`PTR<VOID>`), like C `fopen`. Throws `FileOpenException` on failure. |
| `fclose(f)` | Closes a file handle. Throws `FileCloseException` on failure. |
| `fread(buffer, size, count, f)` | Reads from a file into `buffer`, returns bytes read (`INT64`). |
| `fwrite(buffer, size, count, f)` | Writes `buffer` to a file, returns bytes written (`INT64`). |
| `fseek(f, offset, whence)` | Seeks within a file. Throws `FileSeekException` on failure. |
| `ftell(f)` | Returns the current file position (`INT64`). Throws `FileException` on failure. |
| `feof(f)` | Returns non-zero if the stream has reached end-of-file (`INT32`). |
| `ferror(f)` | Returns non-zero if the stream's error indicator is set (`INT32`). |
| `fgets(buf, n, f)` | Reads up to `n-1` characters into `buf`, stopping at newline or EOF. Returns `buf` on success or `null` on EOF/error. |
| `fputs(str, f)` | Writes a NUL-terminated string to a stream. Returns `INT32`; non-negative on success, EOF on error. |
| `tmpfile()` | Creates a temporary read/write file that is deleted on `fclose`. Returns `PTR<VOID>`. Throws `FileOpenException` on failure. |
| `memcpy(dest, src, count)` | Copies `count` bytes from `src` to `dest`. Both are `PTR<VOID>`/raw pointers. |
| `memset(ptr, value, count)` | Fills the first `count` bytes of `ptr` with `value` (`INT32`). |
| `remove(path)` | Deletes a file. Throws `FileRemoveException` on failure. |
| `rename(oldPath, newPath)` | Renames/moves a file. Throws `FileRenameException` on failure. |
| `mkdir(path, mode)` | Creates a directory. Throws `DirectoryException` on failure. |
| `rmdir(path)` | Removes a directory. Throws `DirectoryException` on failure. |

```zv
PTR<VOID> f = fopen("file.txt", "r");
fseek(f, 0, 2);
INT64 size = ftell(f);
fread(buffer, 1, size as UINT64, f);
fwrite(buffer, 1, size as UINT64, f);
fclose(f);
remove("old.txt");
rename("a.txt", "b.txt");
mkdir("dir", 511);
rmdir("dir");
```

**Strings**

ZV distinguishes between a native length-aware string and a C-style
NUL-terminated string:

* `STRING` — an immutable UTF-8 value represented as `{ i8* data, i64 len }`.
  Its length is available in O(1) via `len(s)`, and string literals produce
  `STRING` values. `STRING` does not guarantee a NUL terminator (e.g. a
  concatenation result generally isn't NUL-terminated), and its bytes may
  contain embedded NUL characters.
* `CSTRING` — a plain, NUL-terminated `i8*` used at the C/native boundary,
  never automatically freed on its own. Convert a `STRING` to a `CSTRING`
  with `cstr(s)`.
* `WSTRING` — a NUL-terminated UTF-16 `i16*` used for Windows wide-character
  APIs. Convert a `STRING` or `CSTRING` to a `WSTRING` with `wstr(s)`.
  Currently only supported when targeting Windows (it uses
  `MultiByteToWideChar(CP_UTF8)`).

| Function | Description |
|----------|-------------|
| `len(s)` | Returns the byte length of a `STRING` or dynamic array (`INT64`). O(1) for `STRING`. |
| `cstr(s)` | Converts a `STRING` to a `CSTRING` by allocating a fresh, NUL-terminated heap copy of its bytes. Passing an existing `CSTRING` returns it unchanged (no allocation). See lifetime rules below. |
| `wstr(s)` | Converts a `STRING` or `CSTRING` to a `WSTRING` by allocating a fresh, NUL-terminated UTF-16 copy. Passing an existing `WSTRING` returns it unchanged. Lifetime is handled the same as `cstr()`. |

`STRING` supports `+` for concatenation and `==` / `!=` for content equality.
Concatenation allocates a new buffer. When the result is assigned to a
variable, it is owned and freed automatically at the end of that variable's
scope; otherwise it should be freed explicitly with `free()` if it is kept.

**`cstr()` / `wstr()` allocation lifetime**

`cstr(s)` and `wstr(s)` both make defensive, NUL-terminated copies when `s`
is a `STRING` (neither assumes the source buffer is NUL-terminated), so the
new allocation needs an owner:

* **Bound directly to a `CSTRING` or `WSTRING` variable** —
  `CSTRING p = cstr(s);` or `WSTRING w = wstr(s);` — the allocation is
  owned by the variable and freed automatically at the end of its scope,
  just like `strdup()`/`str_concat()`.
* **Used inline without being bound to a variable** — e.g. as a call
  argument, `f(cstr(s))` — it is a ZV-managed temporary: the compiler
  frees it automatically once the statement that created it finishes
  evaluating, so it does not need to be freed manually.
* **Returned or stored somewhere else long-lived** (e.g. into a struct
  field or an array) is not automatically tracked; free it explicitly
  with `free()` once you're done with it.

```zv
STRING name = "ZV";
print(len(name));            // 2
print(name == "ZV");         // true

STRING greeting = "Hello, " + name;
print(greeting);             // prints "Hello, ZV"
free(greeting);
```

**C-string interop**

When calling a native API, use `CSTRING` parameters and convert explicitly:

```zv
extern "user32.dll" {
    INT32 MessageBoxA(PTR<VOID> hwnd, CSTRING text, CSTRING caption, UINT32 type);
}

STRING message = "Hello from ZV";
MessageBoxA(0, cstr(message), cstr("ZV"), 0);
```

For Windows UTF-16 APIs, use `WSTRING` and `wstr()`:

```zv
extern "user32.dll" {
    INT32 MessageBoxW(PTR<VOID> hwnd, WSTRING text, WSTRING caption, UINT32 uType);
}

STRING message = "Hello from ZV";
MessageBoxW(null, wstr(message), wstr("ZV"), 0);
```

Here, both `cstr(message)` and `cstr("ZV")` are ZV-managed temporaries: the
compiler frees them automatically right after the `MessageBoxA` call
statement finishes, so nothing needs to be freed by hand. If you want to
keep a converted `CSTRING` or `WSTRING` around instead, bind it to a variable
and it becomes owned:

```zv
CSTRING text = cstr(message);   // owned; freed automatically at end of scope
MessageBoxA(0, text, cstr("ZV"), 0);
```

For raw C-string manipulation, the following functions are also available on
`CSTRING` values:

| Function | Description |
|----------|-------------|
| `strlen(s)` | Returns the length of `s` in bytes, scanning for NUL (`INT64`). |
| `strcmp(a, b)` | Byte-wise comparison, like C `strcmp` (`INT32`: 0 if equal). |
| `strncmp(a, b, n)` | Like `strcmp`, but compares at most `n` bytes. |
| `strcpy(dest, src)` | Copies `src` (including the NUL terminator) into `dest`. `dest` must already have enough space. Returns `dest`. |
| `strncpy(dest, src, n)` | Copies at most `n` bytes of `src` into `dest`. Returns `dest`. |
| `strcat(dest, src)` | Appends `src` to the end of `dest`. `dest` must have enough space. Returns `dest`. |
| `strncat(dest, src, n)` | Appends at most `n` bytes of `src` to `dest`. Returns `dest`. |
| `strchr(s, ch)` | Returns a pointer to the first occurrence of `ch` in `s`, or `null` if not found. |
| `strstr(haystack, needle)` | Returns a pointer to the first occurrence of `needle` in `haystack`, or `null` if not found. |
| `strdup(s)` | Allocates and returns a new heap copy of `s`. Throws `OutOfMemoryException` on allocation failure; the caller owns the result. |

### Threads and Concurrency

These functions are available only for the hosted `exe` and `lib` targets. On
Windows the compiler emits native Win32 thread APIs; on Linux and macOS it emits
POSIX pthreads. The linker automatically pulls in the platform threading library.

All thread and mutex handles are opaque `PTR` values returned from the creation
functions. They are **not** automatically garbage-collected: call `thread_join`
and `mutex_destroy` to release them.

| Function | Description |
|----------|-------------|
| `thread_spawn(STRING fn, PTR arg)` | Starts a new OS thread that calls the ZV function named `fn` with `arg`. The worker must be declared as `VOID fn(PTR arg)`. Returns a thread handle (`PTR`). |
| `thread_join(PTR handle)` | Blocks until the thread exits and frees the handle. |
| `thread_sleep_ms(INT32 ms)` | Suspends the calling thread for `ms` milliseconds. |
| `mutex_create()` | Creates a new OS mutex and returns its handle (`PTR`). |
| `mutex_lock(PTR handle)` | Acquires the mutex, blocking if it is already held. |
| `mutex_unlock(PTR handle)` | Releases the mutex. |
| `mutex_destroy(PTR handle)` | Destroys the mutex and frees the handle. |
| `atomic_load_int8(PTR p)` ... `atomic_load_uint128(PTR p)` | Atomically reads the integer at `p`. Available for all integer types: `int8`, `uint8`, `int16`, `uint16`, `int32`, `uint32`, `int64`, `uint64`, `int128`, `uint128`. |
| `atomic_store_<type>(PTR p, <type> v)` | Atomically writes `v` to the integer at `p` for the matching type. |
| `atomic_add_<type>(PTR p, <type> v)` | Atomically adds `v` to the integer at `p` and returns the previous value. |

These are raw primitives: you are responsible for avoiding data races. Use a
mutex or atomics when multiple threads access the same memory.

```zv
// Atomic shared counter
VOID worker(PTR p) {
    atomic_add_int32(p, 1);
}

@entry
VOID main(CSTRING[] args) {
    PTR counter = alloc(4 as INT64);
    atomic_store_int32(counter, 0);

    PTR t1 = thread_spawn("worker", counter);
    PTR t2 = thread_spawn("worker", counter);

    thread_join(t1);
    thread_join(t2);

    print("counter = %d\n", atomic_load_int32(counter));
}
```

```zv
// Mutex-protected global counter
PTR mutex;
INT32 counter = 0;

VOID worker(PTR arg) {
    mutex_lock(mutex);
    counter = counter + 1;
    print("count: %d\n", counter);
    mutex_unlock(mutex);
}

@entry
VOID main(CSTRING[] args) {
    mutex = mutex_create();

    PTR t1 = thread_spawn("worker", null);
    PTR t2 = thread_spawn("worker", null);

    thread_join(t1);
    thread_join(t2);

    mutex_destroy(mutex);
}
```

### Terminal UI (curses)

Available only for the hosted `exe` target. Links `ncurses` on Linux/macOS or
`pdcurses` on Windows. All functions operate on the implicit `stdscr` window.

| Function | Description |
|----------|-------------|
| `curses_init()` | Initializes the screen (`initscr`). Must be called first. |
| `curses_end()` | Restores the terminal (`endwin`). Must be called before exit. |
| `curses_refresh()` | Flushes pending updates to the physical screen. |
| `curses_clear()` | Clears the screen and forces a full repaint on the next refresh. |
| `curses_erase()` | Clears the screen without forcing a full repaint. |
| `curses_echo()` / `curses_noecho()` | Enables/disables echoing of typed characters. |
| `curses_cbreak()` / `curses_nocbreak()` | Enables/disables cbreak mode (input available without waiting for newline). |
| `curses_raw()` | Enables raw mode (disables signal generation for control characters). |
| `curses_start_color()` | Initializes color support. |
| `curses_move(row, col)` | Moves the cursor to `(row, col)`. |
| `curses_printw(fmt, ...)` | Prints a formatted string at the current cursor position. |
| `curses_mvprintw(row, col, fmt, ...)` | Moves to `(row, col)` then prints a formatted string. |
| `curses_addch(ch)` | Writes a single character at the current cursor position. |
| `curses_getch()` | Reads a single character of input (`INT32`), blocking unless `curses_nodelay` is set. |
| `curses_curs_set(visibility)` | Sets cursor visibility (0 = invisible, 1 = normal, 2 = very visible). |
| `curses_keypad(enabled)` | Enables/disables interpretation of function/arrow keys as single key codes. |
| `curses_nodelay(enabled)` | Enables/disables non-blocking `curses_getch()`. |
| `curses_init_pair(pair, fg, bg)` | Defines color pair `pair` with foreground/background colors. |
| `curses_color_pair(pair)` | Returns the attribute value for color pair `pair`, for use with `attron`/`attroff`. |
| `curses_attron(attrs)` / `curses_attroff(attrs)` | Turns the given attribute(s) on/off for subsequent output. |
| `curses_box(verch, horch)` | Draws a border around the window using `verch`/`horch` as the vertical/horizontal characters. |
| `curses_rows()` | Returns the terminal's row count (`LINES`). |
| `curses_cols()` | Returns the terminal's column count (`COLS`). |

```zv
curses_init();
curses_start_color();
curses_init_pair(1, 7, 0);
curses_attron(curses_color_pair(1));
curses_mvprintw(5, 5, "Hello from ZV!");
curses_attroff(curses_color_pair(1));
curses_refresh();
curses_getch();
curses_end();
```

### Bare Metal / Kernel

Available only for `-target os-x86`. This target is mostly a tech demo: it shows
the same frontend can emit a freestanding x86 kernel, but it is not meant as a
production kernel toolchain.

**CPU / interrupts / raw memory**

| Function | Description |
|----------|-------------|
| `halt()` | Executes `hlt`. |
| `cli()` | Executes `cli` (disable interrupts). |
| `sti()` | Executes `sti` (enable interrupts). |
| `port_out8/16/32(port, value)` | Writes an 8/16/32-bit value to an I/O port. |
| `port_in8/16/32(port)` | Reads an 8/16/32-bit value from an I/O port. |
| `volatile_read(ptr)` | Performs a volatile load through `ptr`. |
| `volatile_write(ptr, value)` | Performs a volatile store of `value` through `ptr`. |

**Serial (UART 8250/16550)**

| Function | Description |
|----------|-------------|
| `serial_init(port)` | Initializes the UART at `port` (38400 baud, 8N1, FIFO enabled). |
| `serial_write_char(port, ch)` | Writes a single character, blocking until the transmitter is ready. |
| `serial_write(port, str)` | Writes a NUL-terminated string, one character at a time. |
| `serial_read_char(port)` | Blocks until a byte is available, then returns it (`UINT8`). |
| `serial_has_data(port)` | Returns `true` if a byte is available to read without blocking. |

**VGA text mode** (writes directly to the 0xB8000 text buffer, 80x25)

| Function | Description |
|----------|-------------|
| `vga_putc(col, row, ch, color)` | Writes one character and color/attribute byte at `(col, row)`. |
| `vga_clear(color)` | Fills the entire screen with spaces using `color`. |
| `vga_print(col, row, str, color)` | Writes a string starting at `(col, row)`, wrapping to the next row. |

**PS/2 keyboard controller (8042)**

| Function | Description |
|----------|-------------|
| `ps2_has_data()` | Returns `true` if the controller's output buffer has data waiting. |
| `ps2_read_data()` | Blocks until data is available, then reads a byte from the data port (0x60). |
| `ps2_write_data(byte)` | Writes a byte to the data port (0x60). |
| `ps2_send_command(byte)` | Writes a byte to the command/status port (0x64). |
| `ps2_scancode_to_ascii(scancode)` | Maps a Scan Code Set 1 make-code to its US QWERTY ASCII value (0 if unmapped). |
| `keyboard_getchar()` | Blocks until a printable key is pressed (ignoring key-releases and non-printable keys) and returns its ASCII value (`CHAR`). |

**Linear framebuffer** (parsed from the Multiboot info structure; requires a bootloader such as GRUB that sets up a video mode)

| Function | Description |
|----------|-------------|
| `fb_available()` | Returns `true` if the bootloader reported a usable framebuffer. |
| `fb_width()` / `fb_height()` | Returns the framebuffer's width/height in pixels (`INT32`). |
| `fb_pitch()` | Returns the number of bytes per scanline (`INT32`). |
| `fb_bpp()` | Returns the bits per pixel (`INT32`); 32bpp and 16bpp are supported by the pixel-writing builtins. |
| `fb_set_pixel(x, y, color)` | Sets the pixel at `(x, y)` to `color`. |
| `fb_fill_rect(x, y, w, h, color)` | Fills a `w`x`h` rectangle starting at `(x, y)` with `color`. |
| `fb_clear(color)` | Fills the entire framebuffer with `color`. |

```zv
halt();      // HLT instruction
cli();       // Clear interrupts
sti();       // Enable interrupts

port_out8(0x3F8, 'A');
UINT8 b = port_in8(0x3F8);

UINT8 v = volatile_read(mmio_ptr);
volatile_write(mmio_ptr, 0xFF);

serial_init(0x3F8);
serial_write(0x3F8, "Hello, serial!\n");
UINT8 ch = serial_read_char(0x3F8);

vga_clear(0x0F);
vga_putc(0, 0, 'H', 0x0F);
vga_print(0, 1, "Hello, VGA!", 0x0F);

CHAR key = keyboard_getchar();
UINT8 sc = ps2_read_data();

if (fb_available()) {
    fb_clear(0x000000);
    fb_set_pixel(100, 100, 0xFF0000);
    fb_fill_rect(200, 200, 50, 50, 0x00FF00);
}
```

---

## Exception Handling

ZV has runtime exception handling via `try`/`catch`/`throw`. Exceptions are
values of the built-in `Exception` type, which carries a message string.

### Creating Exceptions

```zv
Exception err = Exception("something went wrong");
Exception HttpNotFound = Exception("HTTP request got 404");
```

### throw

`throw` raises an exception. If no `try`/`catch` block is active the program
prints the message to stderr and exits with code 1.

```zv
throw Exception("fatal error");
```

You can also throw a plain string literal directly, without wrapping it in
`Exception(...)`:

```zv
throw "file not found";
```

### try / catch

```zv
try {
    PTR<VOID> f = fopen("missing.txt", "r");
} catch (e) {
    print("Caught: %s", e.message);
}
```

The variable declared in `catch (e)` (no type) is an `Exception` whose
`.message` field contains the error string, and it catches *any* exception.
`catch (Exception e)` behaves exactly the same way - `Exception` is the
built-in catch-all type.

### Custom exception types

Declare a named, catchable exception type with `exception Name;`:

```zv
exception NegativeBalanceException;

VOID withdraw(FLOAT64 amount) {
    if (amount < 0.0) {
        throw NegativeBalanceException("cannot withdraw a negative amount");
    }
}
```

Once declared, `Name("description")` constructs a tagged exception (just
like the built-in `Exception("...")`), and `catch (Name e)` only catches
exceptions of that type, leaving others to propagate:

```zv
try {
    withdraw(-5.0);
} catch (NegativeBalanceException e) {
    print("Rejected: %s", e.message);
} catch (Exception e) {
    print("Some other error: %s", e.message);
}
```

A `try` can have multiple `catch` clauses; they're tried in order, and a
catch-all (`catch (e)` or `catch (Exception e)`) must come last if present.
If no clause matches, the exception propagates to an enclosing `try`, or
aborts the program if there is none - exactly like an unhandled `throw`.

You can give an exception type a default message with
`exception Name = <message>;`, so it can be thrown/constructed without
repeating it every time:

```zv
exception PoopException = Exception("the program shitted itself");

throw PoopException;              // uses the default message
throw PoopException("override");  // or supply your own
```

Under the hood, every runtime exception - built-in or user-declared - is
just a message string with a `"TypeName: description"` prefix; `catch`
matches against that prefix. That means `throw "MyError: oops";` works even
without an `exception MyError;` declaration, and if you *do* declare the type,
`catch (MyError e)` will catch it the same way it would catch
`MyError("oops")`. Using `catch (MyError e)` without declaring
`exception MyError;` first is a compile error.

### Runtime Exceptions from Builtins

Many built-in functions automatically throw runtime exceptions on failure.
These are pre-declared exception types, so they can be both caught *and*
constructed/thrown by name just like a custom exception type:

| Builtin | Exception |
|---------|-----------|
| `fopen` | `FileOpenException: failed to open file` |
| `fclose` | `FileCloseException: failed to close file` |
| `fseek` | `FileSeekException: fseek failed` |
| `ftell` | `FileException: ftell failed` |
| `remove` | `FileRemoveException: failed to remove file` |
| `rename` | `FileRenameException: failed to rename file` |
| `mkdir` | `DirectoryException: failed to create directory` |
| `rmdir` | `DirectoryException: failed to remove directory` |
| `alloc` | `OutOfMemoryException: memory allocation failed` |

These can all be caught with `try`/`catch`, either generically or by type:

```zv
try {
    PTR<VOID> f = fopen("/nonexistent", "r");
} catch (FileOpenException e) {
    print("Couldn't open file: %s", e.message);
} catch (Exception e) {
    print("Other error: %s", e.message);
}
```

---

## Processes: respawn()

`respawn()` is ZV's cross-platform alternative to `fork()`. A real `fork()`
can't be given identical semantics on Windows - there is no way to duplicate a
running process's address space there - so instead of a POSIX-only `fork()`
with a different (thread- or re-exec-based) story on Windows, `respawn()`
gives *every* platform the same, more restricted, contract:

```zv
PROCESS p = respawn();
if (p.child) {
    print("I'm the child");
    exit(0);
}
print("I'm the parent");
```

`respawn()` relaunches this same executable, with its original command-line
arguments, as a brand new OS process (`fork()`+`execvp()` on Linux/macOS,
`_spawnvp()` on Windows) and returns `PROCESS { BOOL child }` to the caller
(`child = false`). The freshly launched process runs the program again from
its entry point (`@entry`); when *that* process's code reaches a `respawn()`
call, it recognizes it's the relaunched instance and returns
`PROCESS { child: true }` immediately, without spawning anything further.

**This is not a real `fork()`** - there is no address-space duplication.
Concretely:

* Everything the program does *before* the `respawn()` call runs twice: once
  in the original process, and again from scratch in the newly-started
  process. Only put idempotent setup before the call.
* The "child" does not inherit the parent's local variables, open sockets, or
  call stack at the point of the call - it starts over at `@entry` with the
  same `args` (respawn's internal marker argument is filtered out of `args`
  automatically).
* Because of the above, `respawn()` cannot be used to fork off a worker
  per-iteration of a loop the way `fork()` classically is (e.g. one child per
  accepted socket connection) - the child would just re-run the whole program,
  including re-listening on its own socket, not take over a specific
  already-accepted connection. It's suited to patterns like "re-run the risky
  or heavy part of this program in an isolated process."
* `exit(code)` (a thin wrapper over the C `exit()`) ends whichever process
  calls it; it's the usual way to end the child branch, matching the example
  above.

`respawn()` and `exit()` require a hosted OS process and are rejected at
compile time when targeting a freestanding/kernel target (`-target os-x86`).

---

## Safety: bounds checking and unsafe

I chose to bounds-check every array access by default. The runtime cost is tiny
compared to the debugging time it saves, and when I genuinely need unchecked
memory access I can put it inside an explicit `unsafe { ... }` block so the
danger is visible in the source.

Array indexing is bounds-checked by default:

* **Fixed-size arrays (`T[N]`)** — a constant (literal) out-of-range index is
a **compile-time error**. A non-constant index gets a runtime check.
* **Dynamic arrays (`T[]`)** — every index is checked at runtime against the
array's length before the memory access happens.

A failed runtime check raises an `IndexOutOfBoundsException` through the same
mechanism used by other runtime errors (see [Exception Handling](#exception-handling));
if there is no enclosing `try`/`catch`, the program prints the error and exits.

```zv
INT32[4] a;
a[10] = 5;          // compile error: index 10 out of bounds for length 4

INT32[] nums = INT32[10];
INT32 i = 100;
nums[i] = 1;        // runtime IndexOutOfBoundsException
```

Raw pointers (`PTR<VOID>`, and any other pointer-typed value) carry no length
information, so they can never be bounds-checked. Indexing a raw pointer, or
converting between a pointer and an integer with `as`, therefore requires an
explicit `unsafe { }` block. This keeps unchecked memory access visible in
source, while safe ZV code (typed arrays, structs, references) is always
protected:

```zv
VOID poke(PTR<VOID> p) {
    // p[0] = 1;             // compile error: requires 'unsafe'
    // UINT64 addr = p as UINT64; // compile error: requires 'unsafe'

    unsafe {
        p[0] = 1;
        UINT64 addr = p as UINT64;
    }
}
```

`unsafe { }` blocks may be nested and only affect the checks above; every
other rule (types, struct access, etc.) still applies inside them.

### Meaningful `unsafe` examples

`unsafe` is intended for the small amount of code that genuinely needs unchecked
memory access — talking to hardware, parsing binary protocols, or implementing
low-level data structures. The pointer casts themselves do not require `unsafe`;
only the unchecked indexing and integer↔pointer conversions do.

**Viewing an array's raw bytes**

A fixed-size array can be decayed to a byte pointer. This is useful for
serialization, hashing, or inspecting endianness:

```zv
VOID dump_first_bytes() {
    INT32[3] values = [10, 20, 30];

    // Cast to a byte pointer; the cast itself does not require unsafe.
    PTR<INT8> bytes = values as PTR<INT8>;

    unsafe {
        print(bytes[0] as INT32);   // 10 (first byte of values[0])
        print(bytes[4] as INT32);   // 20 (first byte of values[1])
        bytes[8] = 99;              // mutate values[2] through the alias
    }

    print(values[2]);               // 99
}
```

**Manually allocated raw buffer**

`alloc()` returns an owning `PTR<VOID>`. You can treat it as a byte buffer and
free it when done:

```zv
VOID manual_buffer() {
    PTR<VOID> raw = alloc(8 as INT64);   // allocate 8 bytes

    unsafe {
        raw[0] = 0x41;                   // 'A'
        raw[1] = 0x42;                   // 'B'
        print(raw[0] as INT32);          // 65
        print(raw[1] as INT32);          // 66
    }

    free(raw);                           // early release of untyped memory
}
```

**Pointer / integer round-trip**

Some APIs hand back a numeric handle that must later be used as a pointer. The
round-trip is allowed inside `unsafe`:

```zv
unsafe {
    INT32[3] values = [10, 20, 30];
    PTR<INT8> p = values as PTR<INT8>;

    UINT64 addr = p as UINT64;            // encode pointer as integer
    PTR<INT8> back = addr as PTR<INT8>;   // decode back to pointer
    print(back[4] as INT32);              // 20
}
```

Because raw pointer indexing is not bounds-checked, it is the programmer's
responsibility to keep offsets within the intended object. Mistakes here can
read or write adjacent memory without a runtime error.

ZV also tracks a basic ownership state for `free()`/`move()`'d variables: using
a variable after it has been freed or moved away is a compile-time error, and
so is freeing the same variable twice. Reassigning the variable makes it valid
again.

```zv
INT32[] nums = INT32[10];
free(nums);
nums[0] = 1;   // compile error: 'nums' was already freed
free(nums);    // compile error: 'nums' was already freed
```

Because heap allocations are freed automatically when the owning variable goes
out of scope, explicit `free()` is no longer required for the common case. It
remains useful for early release or for values whose lifetime must end before
their declaring scope exits:

```zv
VOID example() {
    INT32[] buffer = INT32[1024];
    // buffer is freed automatically at the end of this block
}

VOID early() {
    INT32[] buffer = INT32[1024];
    free(buffer);   // early release is still allowed
}
```

This tracking is flow-insensitive (it does not reason about `if`/`while`
branches independently), so it is a simple, conservative approximation rather
than a full borrow checker — it is meant to catch the common straight-line
use-after-free/double-free/double-move mistakes.

---

## Ownership: move and copy

I don't want a garbage collector, but I also don't want to chase manual-memory
bugs in every program. ZV provides explicit ownership-transfer builtin
functions, and the compiler tracks three states for every variable that owns a
resource:

* **valid / owned** — the variable holds a live value.
* **moved** — ownership was transferred out via `move()`.
* **freed** — the resource was released via `free()`.

A moved or freed variable cannot be used until it is reassigned.

### move(x)

Returns the value of `x` and invalidates the source variable (ownership
transfer). After `move`, the original variable is conceptually invalid and
using it again (without reassigning it first) is a compile-time error.

The language-level guarantee is **compile-time invalidation** — the source is
dead after a move. Runtime zeroing is an implementation detail for debugging
safety, not the semantic meaning. A moved-from variable is invalid, not
zero-valued.

```zv
INT32[] a = INT32[10];
INT32[] b = move(a);

a[0] = 1;          // compile-time error: 'a' was moved
```

`move()` is optional when returning a local value — `return numbers;` already
transfers ownership out of the function the same way `return move(numbers);`
does; the explicit form just documents intent.

It is also optional in plain assignment when the right-hand side is an owning
variable. `INT32[] b = a;` is equivalent to `INT32[] b = move(a);` and
invalidates `a`.

### copy(x)

Returns a bitwise copy of `x`. The original remains valid.

**`copy()` is only valid for trivially-copyable (non-owning) values.** Copying
an owned resource (such as a heap-allocated dynamic array) would silently
create two owners of the same memory, causing double-free. The compiler
rejects this at compile time:

```zv
INT32 a = 42;
INT32 b = copy(a);   // OK: INT32 is trivially copyable

INT32[] nums = INT32[10];
INT32[] dup = copy(nums);  // compile-time error: cannot copy owned variable
```

If a deep copy is needed for a resource-owning value, allocate explicitly:

```zv
INT32[] original = INT32[10];
INT32[] duplicate = INT32[10];   // allocate new memory, then copy elements
```

This keeps the distinction between bitwise copy, ownership transfer, and deep
resource copy always explicit in the source.

### Ownership through structs

A struct is a heap-owning ("owning") type if any of its fields is a dynamic
array (`T[]`), a `CSTRING`, or another owning struct - transitively, to
arbitrary depth:

```zv
struct A {
    INT32[] data;
}

struct B {
    A a;
    CSTRING name;
}
```

`B` owns memory (through `a.data` and `name`), so assigning into one of these
fields is an ownership transfer, exactly like binding a fresh allocation to a
variable. When an owning struct variable's scope ends (or it is `free()`'d or
overwritten), the compiler recursively destroys its owning fields - `A.data`
and `B.name` above - the same way it would free a plain `T[]` or `CSTRING`
variable:

```zv
VOID demo() {
    B b;
    b.a.data = INT32[10];
    b.name = cstr("hello");
    // both b.a.data and b.name are freed automatically here.
}
```

Fields never assigned an allocation stay null/zero (struct locals are
zero-initialized), and `free(NULL)` is always a safe no-op, so this is safe
even if only some owning fields are ever populated.

Because an owning field is always considered owned by its containing struct,
storing a borrowed/shared array or `CSTRING` pointer in one is not supported;
allocate (or `cstr()`/`strdup()`) a fresh copy instead, or use a raw
`PTR<T>` field with `unsafe { }` if you truly need a non-owning pointer.

Any assignment that reads an already-owned owning value — whether it is a
plain variable (`B b2 = b;` or `b2 = b;`) or an owning field of an owned struct
(`INT32[] arr = foo.data;` or `bar.field = foo.data;`) — is treated as an
**implicit `move()`**. Ownership transfers to the left-hand side, and the
source variable/field becomes invalid; using a moved-from variable again without
reassigning it is a compile-time error. This guarantees that there is never more
than one owner of any heap allocation and that an owning value can never become a
dangling shallow alias when its source is destroyed.

`copy()` is rejected for any value that owns heap memory (an owned dynamic
array, `CSTRING`, or owning struct). If you want two independent copies,
allocate a new value and copy the contents explicitly, for example with
`array_copy()` for arrays.

```zv
struct Foo {
    INT32[] data;
}

struct Point {
    INT32 x;
    INT32 y;
}

VOID demo() {
    Foo a;
    a.data = INT32[10];
    Foo b = a;          // implicit move: a is now invalid, b owns the data
    a.data[0] = 1;      // compile-time error: 'a' was moved

    Foo c = copy(b);    // compile-time error: cannot copy() owned variable 'b'

    Point p;
    Point q = copy(p);  // OK: Point has no owning fields
}
```

---

## Type Aliases

### type (transparent alias)

Creates a new name for an existing type. The alias is interchangeable with the
original type.

```zv
type Size = UINT64;
type Str = STRING;

Size n = 1024;
```

### newtype (distinct alias)

Creates a distinct type backed by an existing type. It shares the same
representation as its underlying type at the IR level, but the compiler
enforces that it is *not* interchangeable with that underlying type or with
any other newtype - crossing the boundary always requires an explicit `as`
cast.

```zv
newtype Celsius = FLOAT64;
newtype Fahrenheit = FLOAT64;

Celsius c = 20.0;      // OK: numeric literals may initialize a newtype directly
Fahrenheit f = 68.0;

c = f;                    // ERROR: distinct newtypes
Celsius x = f;            // ERROR: distinct newtypes
FLOAT64 y = c;            // ERROR: newtype -> underlying type is not implicit
FLOAT64 y = c as FLOAT64; // OK: explicit conversion to the underlying type
Celsius x2 = y as Celsius; // OK: explicit conversion from the underlying type

Fahrenheit f2 = c as Fahrenheit; // ERROR: casting between distinct newtypes
                                 // is rejected even with `as` - write a
                                 // conversion function instead.
```

The same rule applies to function parameters and return values:

```zv
VOID set_temperature(Celsius value) {
    ...
}

Fahrenheit f = 72.0;
set_temperature(f); // ERROR: Fahrenheit is not a Celsius
```

`type` aliases remain fully transparent and are unaffected by these checks.

---

## Compilation Targets

### `exe` (Hosted)

* Linked with Clang against `user32`, `kernel32`, `msvcrt`, and
`legacy_stdio_definitions`.
* `extern` libraries are appended as `-l<name>`. Each one is printed to the
console (e.g. `Linking against native library 'shell32.dll' (-lshell32)`),
along with the full `clang` command line that is actually run, so it's always
clear which DLLs/`.so` files the output depends on.
* Kernel-only builtins and `os-x86` are rejected.

### `lib` (Shared Library)

* Compiled the same as `exe`, but linked with Clang using `-shared` (plus
`-fPIC` on non-Windows) instead of producing an executable.
* Defaults to a `.dll` output on Windows and `.so` on other platforms when
`-o` isn't given.
* Functions marked `export` get external linkage and (on Windows) the LLVM
`dllexport` storage class, so they are visible in the resulting DLL/SO's symbol
table.
* Every other top-level function gets `internal` linkage — it can still be
called from other ZV functions in the same module, but it is not part of the
library's public ABI and won't show up as an exported symbol.
* `@entry` functions are not required for a `lib` build.

### `os-x86` (Freestanding Kernel — tech demo)

* Emits a Multiboot v1 header (section `.multiboot`) and an x86 `_start` entry
point. This target is mostly a proof-of-concept; it shows the same frontend can
reach bare metal, but it is not a serious production kernel toolchain.
* Compiles to `-target i686-unknown-none-elf -ffreestanding -m32 -c` and links
with `ld.lld` and a custom linker script placing the image at 1 MiB.
* No libc, no CRT, no `extern` declarations.
* Framebuffer information is parsed from the Multiboot info structure passed by
the bootloader.
* QEMU is launched with `-serial stdio` so `serial_write` output appears in the
terminal.

---

## Examples

### Hello World

```zv
@entry
UINT32 main(CSTRING[] args) {
    print("Hello, world!");
    return 0;
}
```

### Arithmetic, Loops, and Arrays

```zv
@entry
UINT32 main(CSTRING[] args) {
    INT32[] nums = [1, 2, 3, 4, 5];
    INT32 total = 0;

    for (INT32 i = 0; i < len(nums) as INT32; i++) {
        total = total + nums[i];
    }

    print("total: %d", total);
    return 0;
}
```

### Fixed-Size and Heap Arrays

```zv
INT32[] create_buffer() {
    INT32[] buf = INT32[64];
    return buf;   // ownership transferred to caller
}

@entry
UINT32 main(CSTRING[] args) {
    // Stack array: zero-initialized by default
    INT32[4] stack;
    stack[0] = 10;

    // Stack array filled with a value
    INT32[4] filled = 7;

    // Heap array with explicit initializer
    INT32[] heap = INT32[4](5);

    print("stack[0]: %d", stack[0]);
    print("filled[3]: %d", filled[3]);
    print("heap[0]: %d", heap[0]);
    print("heap len: %lld", len(heap));

    INT32[] owned = create_buffer();
    print("owned len: %lld", len(owned));

    // 'heap' and 'owned' are freed automatically when main ends.
    // Explicit free() is only needed for early release.
    return 0;
}
```

### Structs

```zv
struct Point {
    INT32 x;
    INT32 y;
}

INT32 distance_squared(Point p) {
    return p.x * p.x + p.y * p.y;
}

@entry
UINT32 main(CSTRING[] args) {
    Point p;
    p.x = 3;
    p.y = 4;
    print("squared distance: %d", distance_squared(p));
    return 0;
}
```

### Calling a Native Library

```zv
extern "user32.dll" {
    INT32 MessageBoxA(PTR<VOID> hwnd, CSTRING text, CSTRING caption, UINT32 type_val);
}

@entry
UINT32 main(CSTRING[] args) {
    MessageBoxA(0, "Hello from ZV", "ZV", 0);
    return 0;
}
```

### Concurrent Counter with Atomics

```zv
VOID worker(PTR p) {
    atomic_add_int32(p, 1);
}

@entry
VOID main(CSTRING[] args) {
    PTR counter = alloc(4 as INT64);
    atomic_store_int32(counter, 0);

    PTR t1 = thread_spawn("worker", counter);
    PTR t2 = thread_spawn("worker", counter);

    thread_join(t1);
    thread_join(t2);

    print("counter = %d\n", atomic_load_int32(counter));
}
```

### Mutex-Protected Global Counter

```zv
PTR mutex;
INT32 counter = 0;

VOID worker(PTR arg) {
    mutex_lock(mutex);
    counter = counter + 1;
    print("count: %d\n", counter);
    mutex_unlock(mutex);
}

@entry
VOID main(CSTRING[] args) {
    mutex = mutex_create();

    PTR t1 = thread_spawn("worker", null);
    PTR t2 = thread_spawn("worker", null);

    thread_join(t1);
    thread_join(t2);

    mutex_destroy(mutex);
}
```

### Bare-Metal Kernel (tech demo)

```zv
@entry
UINT32 main(CSTRING[] args) {
    serial_init(0x3F8);
    serial_write(0x3F8, "Hello from ZV kernel!\n");

    vga_clear(0x1F);
    vga_print(0, 0, "ZV Kernel", 0x1F);

    while (true) {
        CHAR c = keyboard_getchar();
        serial_write_char(0x3F8, c);
    }

    return 0;
}
```

Build and run:

```bash
dotnet run -- kernel.zv -target os-x86 -o kernel.elf -run
```

---

## Standard Library Helpers

The shipped `lib/` folder contains higher-level helpers built on top of the builtins. Include them with `#include <lib/<name>.zv>`.

| Module | What it covers |
|--------|----------------|
| `lib/file.zv` | Whole-file read/write, streaming, binary primitive I/O, line handling, temp files |
| `lib/path.zv` | Path joining, splitting, extension extraction, normalization, absolute checks |
| `lib/math.zv` | Constants and per-width `min`/`max`/`clamp`/`abs`/`sign`/`lerp`/`round`/`deg`/`rad` helpers |
| `lib/prng.zv` | Fast deterministic 64-bit LCG pseudo-random numbers |
| `lib/secprng.zv` | Deterministic ChaCha20-based CSPRNG |
| `lib/hex.zv` | Hexadecimal encoding and decoding for byte buffers and CSTRINGs |
| `lib/hash/*.zv` | Checksums and hashes: adler32, crc32, djb2, fnv1a, md5, murmur3, sdbm, siphash, xxhash32 |
| `lib/win/*.zv` | Windows API bindings (kept separate from the portable helpers above) |

### File helpers (`lib/file.zv`)

`#include <lib/file.zv>`

Whole-file helpers, streaming, binary primitive I/O, and line handling.

- `readall(path)` — read a whole text file into an owned `CSTRING`.
- `writeall(path, content)` — write text to a file, truncating any existing content.
- `appendall(path, content)` — append text, creating the file if it does not exist.
- `file_size(path)` — return the size of a file in bytes, or `-1` on error.
- `exists(path)` — return `true` if the file can be opened for reading.
- `readallbytes(path)` — read a whole file into an owned `UINT8[]`.
- `readfilebytes(path)` — read a whole file into a `FileBytes { data, len }`; caller must `free(data)`.
- `file_open(path, mode)` / `file_close(fs)` — open/close a `FileStream` (`mode` follows `fopen`).
- `file_read(fs, buf, len)` / `file_write(fs, buf, len)` — raw byte I/O on a stream.
- `file_tell(fs)` / `file_seek(fs, off, whence)` — stream positioning (`whence`: 0=set, 1=cur, 2=end).
- `file_eof(fs)` / `file_error(fs)` — check stream state.
- `file_read_line(fs)` — read one line into an owned `CSTRING`; newline is preserved.
- `file_write_line(fs, line)` — write a `CSTRING` to a stream with `fputs`.
- `tmpfile_stream()` — open a temporary read/write stream deleted automatically on close.
- `readlines(path)` / `file_lines_get(lines, i)` / `file_lines_free(lines)` — read all lines into a `FileLines` view.
- Binary primitive readers/writers (`_le` little-endian, `_be` big-endian): `file_read_u8`, `file_read_i8`, `file_read_u16_le`/`_be`, `file_read_i16_le`/`_be`, `file_read_u32_le`/`_be`, `file_read_i32_le`/`_be`, `file_read_u64_le`/`_be`, `file_read_i64_le`/`_be`, `file_read_f32_le`/`_be`, `file_read_f64_le`/`_be`, and the matching `file_write_*` functions.
- `file_read_cstring(fs, len)` — read exactly `len` bytes into a NUL-terminated `CSTRING`.

### Path helpers (`lib/path.zv`)

`#include <lib/path.zv>`

Small path-manipulation helpers; results are owned `CSTRING`s.

- `path_join(a, b)` — join two path components with a separator when needed.
- `path_dir(p)` — directory portion of a path (`.` if none).
- `path_file(p)` — file-name portion of a path.
- `path_ext(p)` — file extension, or empty string if none.
- `dirname(p)`, `basename(p)`, `extname(p)`, `join(a, b)` — standard aliases for the above.
- `is_absolute(p)` — true for POSIX `/`, Windows drive, or UNC paths.
- `normalize(p)` — collapse `.`/`..`, remove duplicate separators, and use `/` separators.

### Math helpers (`lib/math.zv`)

`#include <lib/math.zv>`

Numeric constants and per-width numeric helpers.

- Constants: `PI`, `TAU`, `E`, `DEG2RAD`, `RAD2DEG`.
- Integer helpers for each signed width (`i32`, `i64`, `i128`): `min_*`, `max_*`, `clamp_*`, `abs_*`, `sign_*`.
- Unsigned helpers for each width (`u32`, `u64`, `u128`): `min_*`, `max_*`, `clamp_*`.
- Float helpers for `f32`/`f64`: `min_*`, `max_*`, `clamp_*`, `abs_*`, `sign_*`, `lerp_*`.
- Conversion/angle helpers: `floor_i32`/`floor_i64`, `ceil_i32`/`ceil_i64`, `round_i32`/`round_i64`, `deg2rad_f32`/`deg2rad_f64`, `rad2deg_f32`/`rad2deg_f64`.

### Pseudo-random helpers (`lib/prng.zv`)

`#include <lib/prng.zv>`

Fast, deterministic 64-bit LCG generator. Not cryptographically secure.

- `prng_seed(seed)` — seed the generator.
- Width-specific outputs: `prng_u8/16/32/64`, `prng_i8/16/32/64`, `prng_bool`.
- Unit-float outputs: `prng_f32()` and `prng_f64()` in `[0, 1]`.
- Ranged variants: `prng_*_range(min, max)`. Integer ranges are inclusive; float ranges are half-open `[min, max)`.

### Secure PRNG helpers (`lib/secprng.zv`)

`#include <lib/secprng.zv>`

Deterministic, platform-independent ChaCha20-based CSPRNG. Still requires a proper entropy source for production security.

- `secprng_seed(seed)` — seed the generator.
- Width-specific outputs: `secprng_u8/16/32/64`, `secprng_i8/16/32/64`, `secprng_bool`.
- Unit-float outputs: `secprng_f32()` and `secprng_f64()` in `[0, 1)`.
- Ranged variants: `secprng_*_range(min, max)`. Integer ranges are inclusive; float ranges are half-open `[min, max)`.

### Hex helpers (`lib/hex.zv`)

`#include <lib/hex.zv>`

Encode byte buffers and CSTRINGs to hex, and decode hex back to `UINT8[]`.

- `hex_encode(data, len)` — encode `len` bytes from `PTR<UINT8>` into a lowercase `CSTRING`.
- `hex_encode_upper(data, len)` — same, using uppercase `A-F`.
- `hex_encode_cstring(s)` — encode a `CSTRING` to lowercase hex.
- `hex_encode_bytes(data)` — encode a `UINT8[]` to lowercase hex.
- `hex_decode(s)` — decode a hex `CSTRING` into an owned `UINT8[]`. Accepts an optional `0x`/`0X` prefix and whitespace between digits. Throws on invalid characters or an odd number of hex digits.

### Hash helpers (`lib/hash/*.zv`)

`#include <lib/hash/<name>.zv>`

Checksum and hash functions; most take a `(PTR<UINT8> data, INT64 len)` buffer and many also offer a `_cstring` variant.

- `adler32(data, len)` / `adler32_cstring(s)` — Adler-32 checksum.
- `crc32(data, len)` / `crc32_cstring(s)` / `crc32_update(crc, data, len)` / `crc32_byte(crc, b)` — CRC-32.
- `djb2(s)` — DJB2 string hash.
- `fnv1a_32(data, len)` / `fnv1a_cstring_32(s)` — FNV-1a 32-bit.
- `md5(data, len, out)` / `md5_cstring(s, out)` — MD5; `out` is a `PTR<UINT8>` to a 16-byte buffer.
- `murmur3_32(data, len, seed)` / `murmur3_cstring_32(s, seed)` — MurmurHash3 32-bit.
- `sdbm(s)` — SDBM string hash.
- `siphash_2_4(data, len, key)` / `siphash_cstring_2_4(s, key)` — SipHash-2-4; `key` is a `UINT8[16]`.
- `xxhash32(data, len, seed)` / `xxhash32_cstring(s, seed)` — xxHash 32-bit.

## Development

* `dotnet build` — Build the compiler.
* `dotnet test` — Run the parser and backend test suite.
* `ZV --lsp` — Run the language server over stdio.

### Language Server

`ZV --lsp` runs a stdio-based LSP server for use with editors and the VS Code
extension. It supports:

* Full document synchronization (`textDocument/didOpen`, `didChange`, `didClose`)
* Diagnostics (lex/parse errors and compile errors) published via
  `textDocument/publishDiagnostics`
* `textDocument/references` for finding symbol usages
* `textDocument/definition` for jumping to a symbol declaration, or jumping to
  the target file of an `#include` directive (local `"..."` and system `<...>`
  includes)

### VS Code Extension

A first-party VS Code extension lives in `ZVCodeExtension/`. It provides syntax
highlighting, language-server diagnostics, and a command to compile the current
`.zv` file. The compiler is auto-detected on `PATH` (looks for `ZV` or `ZV.exe`), or
you can set the absolute path with the `zv.executablePath` setting.

```bash
cd ZVCodeExtension
npm install
npm run compile
```

Open `ZVCodeExtension/` in VS Code and press `F5` to launch an Extension Development
Host window. To build a `.vsix` archive:

```bash
cd ZVCodeExtension
npm run package
code --install-extension zvcode-0.1.0.vsix
```

---

