import re

with open("src/Civil/CivilDrawingBatchExtractor.cs", "r", encoding="utf-8") as f:
    code = f.read()

# Add _rebarMesh field
if "private readonly bool _rebarMesh;" not in code:
    code = code.replace("private readonly bool _preserveNativeDims;", "private readonly bool _preserveNativeDims;\n        private readonly bool _rebarMesh;")
    code = code.replace("bool preflightQa = false, bool enableMarkRepair = false, int fitStage = 0, bool preserveNativeDims = false)", "bool preflightQa = false, bool enableMarkRepair = false, int fitStage = 0, bool preserveNativeDims = false, bool rebarMesh = false)")
    code = code.replace("_preserveNativeDims = preserveNativeDims;", "_preserveNativeDims = preserveNativeDims;\n            _rebarMesh = rebarMesh;")

# Insert RebarMesh logic before macros
rebar_code = """                if (_rebarMesh && !_extractOnly)
                {
                    Console.WriteLine("[RebarMesh] Generating mesh before drawing creation...");
                    var groups = runner.CollectMembers(_markFilter, selectedOnly: _selectedOnly);
                    var meshBuilder = new TeklaExtractor.Model.RebarMeshBuilder(_model);
                    foreach (var part in groups.Walls)
                        meshBuilder.Generate(part);
                    foreach (var part in groups.Columns)
                        meshBuilder.Generate(part);
                    foreach (var part in groups.Beams)
                        meshBuilder.Generate(part);
                }

                if (_extractOnly)"""
code = code.replace("                if (_extractOnly)", rebar_code)

with open("src/Civil/CivilDrawingBatchExtractor.cs", "w", encoding="utf-8") as f:
    f.write(code)
