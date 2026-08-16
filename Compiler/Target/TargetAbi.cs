namespace ZV.Compiler.Target;

public enum TargetAbi
{
    // Hosted ABIs mapped from architecture/OS combinations.
    Cdecl,       // x86-32 hosted (e.g. Windows/Linux 32-bit)
    SysV,        // x86-64 System V (Linux/macOS/ELF)
    MicrosoftX64, // x86-64 Windows

    // Bare-metal ABIs are distinct from hosted ones.
    BareMetalX86_16,
    BareMetalX86_32,
    BareMetalAmd64
}
