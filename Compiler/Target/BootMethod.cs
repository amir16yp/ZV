namespace ZV.Compiler.Target;

public enum BootMethod
{
    None,
    MbrRawBoot16, // x86-16 real-mode boot sector loads a flat kernel image
    Multiboot1, // x86-32 protected-mode ELF loaded by a Multiboot v1 bootloader
    Multiboot2  // x86-64 long-mode ELF loaded by a Multiboot v2 bootloader
}
