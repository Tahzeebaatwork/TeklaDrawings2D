import json

calls = []

with open(r'C:\Users\ASUS\.gemini\antigravity\brain\764512e1-d649-44c7-bb53-55741f1c4176\.system_generated\logs\transcript_full.jsonl', 'r', encoding='utf-8') as f:
    for line in f:
        try:
            data = json.loads(line)
            if 'tool_calls' in data:
                for call in data['tool_calls']:
                    if call.get('name') == 'default_api:run_command' or call.get('name') == 'default_api:replace_file_content':
                        args = call.get('arguments', {})
                        cmd = args.get('CommandLine', '')
                        if 'PrecastDimensionPostProcessor.cs' in cmd:
                            calls.append(cmd)
                        elif call.get('name') == 'default_api:replace_file_content':
                            if 'PrecastDimensionPostProcessor.cs' in args.get('TargetFile', ''):
                                calls.append('REPLACE_FILE_CONTENT: ' + str(args))
        except:
            pass

with open('tool_calls.txt', 'w', encoding='utf-8') as f:
    f.write('\n'.join(calls))
