import json

found_content = None

with open(r'C:\Users\ASUS\.gemini\antigravity\brain\764512e1-d649-44c7-bb53-55741f1c4176\.system_generated\logs\transcript_full.jsonl', 'r', encoding='utf-8') as f:
    for line in f:
        try:
            data = json.loads(line)
            # Look for tool responses containing the file content
            if 'tool_calls' in data:
                continue
            if 'content' in data and 'private static void SealView(' in data['content']:
                if 'LiveLength' in data['content']:
                    found_content = data['content']
            elif 'output' in data and 'private static void SealView(' in data['output']:
                if 'LiveLength' in data['output']:
                    found_content = data['output']
        except:
            pass

if found_content:
    with open('recovered.txt', 'w', encoding='utf-8') as f:
        f.write(found_content)
    print('Found it!')
else:
    print('Not found in this simple check.')
