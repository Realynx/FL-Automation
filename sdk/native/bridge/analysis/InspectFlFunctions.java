// Focused read-only input inspection in a disposable Ghidra project.
// @category FruityLink.Analysis
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.io.BufferedWriter;

public class InspectFlFunctions extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] arguments = getScriptArgs();
        if (arguments.length != 2) {
            throw new IllegalArgumentException("Expected targets.tsv and output.txt");
        }
        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        try (BufferedWriter output = Files.newBufferedWriter(Path.of(arguments[1]), StandardCharsets.UTF_8)) {
            output.write("Program: " + currentProgram.getExecutablePath() + "\n");
            for (String line : Files.readAllLines(Path.of(arguments[0]), StandardCharsets.UTF_8)) {
                if (line.isBlank() || line.startsWith("#")) continue;
                monitor.checkCancelled();
                String[] columns = line.split("\\t");
                Address address = toAddr(Long.parseUnsignedLong(columns[1].replace("0x", ""), 16));
                disassemble(address);
                Function function = getFunctionAt(address);
                if (function == null) function = createFunction(address, columns[0]);
                output.write("\n===== " + columns[0] + " @ " + address + " =====\n");
                if (function == null) {
                    output.write("Function creation failed\n");
                    continue;
                }
                InstructionIterator instructions = currentProgram.getListing().getInstructions(function.getBody(), true);
                int count = 0;
                while (instructions.hasNext() && count++ < 1000) {
                    Instruction instruction = instructions.next();
                    output.write(instruction.getAddress() + "  " + instruction + "\n");
                }
                DecompileResults result = decompiler.decompileFunction(function, 30, monitor);
                if (result.decompileCompleted()) output.write(result.getDecompiledFunction().getC());
                else output.write("Decompilation unavailable: " + result.getErrorMessage() + "\n");
                output.flush();
                println("Inspected " + columns[0] + " @ " + address);
            }
        } finally {
            decompiler.dispose();
        }
    }
}
