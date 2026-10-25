// Apply recovered IL2CPP method names before native analysis.
// @category PCBS2
import java.nio.file.*;
import java.nio.charset.StandardCharsets;
import java.io.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.SourceType;

public class ImportIl2Cpp extends GhidraScript {
    public void run() throws Exception {
        int count = 0, failures = 0;
        try (BufferedReader reader = Files.newBufferedReader(Path.of(getScriptArgs()[0]), StandardCharsets.UTF_8)) {
            reader.readLine();
            String line;
            while ((line = reader.readLine()) != null) {
                monitor.checkCancelled();
                String[] row = line.split("\t", -1);
                long rva = Long.parseUnsignedLong(row[0].substring(2), 16);
                if (rva == 0) continue;
                Address address = currentProgram.getImageBase().add(rva);
                if (!currentProgram.getMemory().contains(address)) continue;
                String signature = row[5].replace(" { }", "");
                String label = (row[3] + "__" + row[4] + "__" + signature).replaceAll("[^A-Za-z0-9_]", "_");
                if (label.length() > 220) label = label.substring(0, 220);
                label += "_" + Long.toHexString(rva);
                try {
                    createLabel(address, label, true, SourceType.USER_DEFINED);
                    Function function = getFunctionAt(address);
                    if (function != null && (function.getSymbol().getSource() != SourceType.USER_DEFINED || row[3].equals("Assembly-CSharp"))) {
                        function.setName(label, SourceType.USER_DEFINED);
                    }
                    setPlateComment(address, row[3] + " :: " + row[4] + " :: " + signature + "\n" + row[6]);
                    if (function == null) addEntryPoint(address);
                    count++;
                } catch (Exception e) { failures++; }
            }
        }
        // PE unwind metadata and the recovered entry points drive function discovery.
        // Avoid external symbol downloads and the quadratic decompiler parameter pass.
        setAnalysisOption(currentProgram, "PDB Universal", "false");
        setAnalysisOption(currentProgram, "Decompiler Parameter ID", "false");
        println("IL2CPP labels applied: " + count + "; failures: " + failures);
    }
}
