with open("src/Program.cs", "r", encoding="utf-8") as f:
    code = f.read()

old_civil = """                bool enableMarkRepair = args != null && HasFlag(args, "--enable-mark-repair");
                bool preserveNativeDims = args != null && HasFlag(args, "--preserve-native-dims");"""
new_civil = """                bool enableMarkRepair = args != null && HasFlag(args, "--enable-mark-repair");
                bool preserveNativeDims = args != null && HasFlag(args, "--preserve-native-dims");
                bool rebarMesh = args != null && HasFlag(args, "--rebar-mesh");"""

code = code.replace(old_civil, new_civil)

old_batch = """                int civilCode = new CivilDrawingBatchExtractor(tekla, baseDir, skipMacros, extractOnly, mark, selectedOnly, toRoot,
                    preflightQa, enableMarkRepair, fitStage, preserveNativeDims).Run();"""
new_batch = """                int civilCode = new CivilDrawingBatchExtractor(tekla, baseDir, skipMacros, extractOnly, mark, selectedOnly, toRoot,
                    preflightQa, enableMarkRepair, fitStage, preserveNativeDims, rebarMesh).Run();"""

code = code.replace(old_batch, new_batch)

with open("src/Program.cs", "w", encoding="utf-8") as f:
    f.write(code)
