// Export every discovered function, retaining a status record for failures.
// @category PCBS2
import java.nio.file.*;
import java.nio.charset.StandardCharsets;
import java.io.*;
import java.util.*;
import java.util.concurrent.atomic.AtomicInteger;
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.app.decompiler.parallel.*;
import ghidra.program.model.listing.*;
import ghidra.util.task.TaskMonitor;

public class ExportNative extends GhidraScript {
    public void run() throws Exception {
        Path root = Path.of(getScriptArgs()[0]);
        Files.createDirectories(root);
        Path methodIndex = root.getParent().getParent().resolve("indexes/methods.tsv");
        runScript("ImportIl2Cpp.java", new String[] { methodIndex.toString() });
        // Headless analysis already scanned all native tables. Here only process
        // newly recovered method entry points, without repeating that broad pass.
        setAnalysisOption(currentProgram, "Create Address Tables", "false");
        analyzeChanges(currentProgram);
        DecompileOptions options = new DecompileOptions();
        options.grabFromProgram(currentProgram);
        options.setDefaultTimeout(60);
        AtomicInteger success = new AtomicInteger(), failed = new AtomicInteger();
        long started = System.currentTimeMillis();
        try (BufferedWriter index = Files.newBufferedWriter(root.resolve("functions.tsv"), StandardCharsets.UTF_8);
             BufferedWriter calls = Files.newBufferedWriter(root.resolve("calls.tsv"), StandardCharsets.UTF_8)) {
            index.write("address\tname\tstatus\tfile\tdetail\n");
            calls.write("caller\tcallee\n");
            DecompilerCallback<Void> callback = new DecompilerCallback<Void>(currentProgram, decompiler -> {
                decompiler.setOptions(options);
                decompiler.toggleCCode(true);
                decompiler.toggleSyntaxTree(false);
            }) {
              public Void process(DecompileResults result, TaskMonitor taskMonitor) throws Exception {
                Function function = result.getFunction();
                if (function.isExternal()) return null;
                String address = function.getEntryPoint().toString();
                String name = function.getName();
                String group = name.contains("__") ? name.substring(0, name.indexOf("__")) : "native-runtime";
                Path folder = root.resolve(group);
                Files.createDirectories(folder);
                Path file = folder.resolve(address + ".c");
                String status, detail = "";
                    if (result.decompileCompleted() && result.getDecompiledFunction() != null) {
                        String text = "/* " + address + " " + name + " */\n";
                        if (function.getComment() != null) text += "/* " + function.getComment().replace("*/", "* /") + " */\n";
                        text += result.getDecompiledFunction().getC();
                        Files.writeString(file, text, StandardCharsets.UTF_8);
                        status = "ok"; success.incrementAndGet();
                    } else {
                        status = "failed"; failed.incrementAndGet();
                        detail = Objects.toString(result.getErrorMessage(), "");
                    }
                synchronized (index) {
                index.write(address + "\t" + name + "\t" + status + "\t" + root.relativize(file).toString().replace('\\', '/') + "\t" + detail.replace('\t', ' ').replace('\n', ' ').replace('\r', ' ') + "\n");
                for (Function callee : function.getCalledFunctions(taskMonitor)) calls.write(address + "\t" + callee.getEntryPoint() + "\n");
                int total = success.get() + failed.get();
                if (total % 1000 == 0) {
                    index.flush(); calls.flush();
                    println("Exported " + total + " functions; failed=" + failed.get() + "; elapsed=" + ((System.currentTimeMillis() - started) / 1000) + "s");
                }
                }
                return null;
              }
            };
            callback.setTimeout(60);
            try {
                ParallelDecompiler.decompileFunctions(callback, currentProgram, currentProgram.getFunctionManager().getFunctions(true), result -> {}, monitor);
            } finally {
                callback.dispose();
            }
        }
        Files.writeString(root.resolve("summary.json"), "{\"ok\":" + success.get() + ",\"failed\":" + failed.get() + "}", StandardCharsets.UTF_8);
        println("Native export complete: ok=" + success.get() + "; failed=" + failed.get());
    }
}
