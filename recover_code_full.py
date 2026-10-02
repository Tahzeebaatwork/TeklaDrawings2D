import json

best_content = ''

def search_dict(d):
    global best_content
    if isinstance(d, dict):
        for k, v in d.items():
            if isinstance(v, str):
                if 'class PrecastDimensionPostProcessor' in v or 'private static void SealView' in v:
                    if len(v) > len(best_content):
                        best_content = v
            else:
                search_dict(v)
    elif isinstance(d, list):
        for item in d:
            search_dict(item)

with open(r'C:\Users\ASUS\.gemini\antigravity\brain\764512e1-d649-44c7-bb53-55741f1c4176\.system_generated\logs\transcript_full.jsonl', 'r', encoding='utf-8') as f:
    for line in f:
        try:
            data = json.loads(line)
            search_dict(data)
        except Exception as e:
            pass

with open('recovered_best.txt', 'w', encoding='utf-8') as f:
    f.write(best_content)
print(f'Max length found: {len(best_content)}')
