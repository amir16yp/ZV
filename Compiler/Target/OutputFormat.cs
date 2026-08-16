namespace ZV.Compiler.Target;

public enum OutputFormat
{
    Executable,
    SharedLibrary,
    ObjectFile,
    RawImage,          // Bare-metal flat binary image
    Elf                // Bare-metal or hosted ELF
}
