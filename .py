#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
扫描 C# 代码，提取所有包含中文的字符串
生成 JSON 语言文件模板
"""

import os
import re
import json
from pathlib import Path
from collections import OrderedDict

# 配置
SOURCE_DIR = "."  # C# 源码目录
OUTPUT_JSON = "extracted_zh-CN.json"
OUTPUT_CSV = "extracted_strings.csv"

# 匹配双引号字符串，内含至少一个中文字符
PATTERN = r'"([^"\\]*(?:\\.[^"\\]*)*)"'  # 匹配所有字符串
CHINESE_PATTERN = re.compile(r'[\u4e00-\u9fff]')  # 检测中文


def has_chinese(text):
    """检查字符串是否包含中文"""
    return bool(CHINESE_PATTERN.search(text))


def extract_strings_from_file(filepath):
    """从单个文件提取中文字符串"""
    results = []
    try:
        with open(filepath, 'r', encoding='utf-8') as f:
            content = f.read()
    except:
        return results

    for match in re.finditer(PATTERN, content):
        string = match.group(1)
        # 处理转义字符
        try:
            string = string.encode().decode('unicode_escape')
        except:
            pass
        
        if has_chinese(string) and len(string.strip()) > 0:
            # 排除注释中的字符串（简单判断）
            line_start = content.rfind('\n', 0, match.start()) + 1
            line = content[line_start:match.start()]
            if '//' in line or line.strip().startswith('*'):
                continue
            
            results.append({
                'string': string,
                'file': os.path.basename(filepath),
                'position': match.start()
            })
    
    return results


def generate_key(string, index, existing_keys):
    """生成唯一的键名"""
    # 根据内容猜测分类
    if any(kw in string for kw in ['文件', '打开', '保存', '选择']):
        prefix = "Dialog"
    elif any(kw in string for kw in ['失败', '错误', '无法', '完成', '成功']):
        prefix = "Message"
    elif any(kw in string for kw in ['正在', '进度', '加载']):
        prefix = "Status"
    else:
        prefix = "Text"
    
    # 生成简短键名
    base_key = f"{prefix}.Item{index}"
    
    # 确保唯一
    key = base_key
    counter = 1
    while key in existing_keys:
        key = f"{base_key}_{counter}"
        counter += 1
    
    return key


def main():
    print(f"扫描目录: {os.path.abspath(SOURCE_DIR)}")
    
    all_strings = {}  # string -> {key, files}
    
    # 扫描所有 .cs 文件
    for root, dirs, files in os.walk(SOURCE_DIR):
        # 排除常见非源码目录
        dirs[:] = [d for d in dirs if d not in ['bin', 'obj', 'packages', '.git', '.vs']]
        
        for file in files:
            if file.endswith('.cs'):
                filepath = os.path.join(root, file)
                results = extract_strings_from_file(filepath)
                
                for item in results:
                    s = item['string']
                    if s not in all_strings:
                        all_strings[s] = {
                            'files': [item['file']],
                            'key': None
                        }
                    else:
                        if item['file'] not in all_strings[s]['files']:
                            all_strings[s]['files'].append(item['file'])
    
    print(f"找到 {len(all_strings)} 个唯一中文字符串")
    
    # 生成键名
    existing_keys = set()
    index = 1
    for string, data in all_strings.items():
        key = generate_key(string, index, existing_keys)
        data['key'] = key
        existing_keys.add(key)
        index += 1
    
    # 生成 JSON（按分类分组）
    json_output = OrderedDict()
    json_output['_meta'] = {'name': '简体中文', 'version': '1.0'}
    
    for string, data in sorted(all_strings.items(), key=lambda x: x[1]['key']):
        key = data['key']
        prefix, name = key.split('.', 1)
        
        if prefix not in json_output:
            json_output[prefix] = OrderedDict()
        
        json_output[prefix][name] = string
    
    # 写入 JSON
    with open(OUTPUT_JSON, 'w', encoding='utf-8') as f:
        json.dump(json_output, f, ensure_ascii=False, indent=2)
    print(f"已生成: {OUTPUT_JSON}")
    
    # 写入 CSV（方便查看和替换）
    with open(OUTPUT_CSV, 'w', encoding='utf-8-sig') as f:
        f.write("原始字符��,键名,替换代码,出现文件\n")
        for string, data in all_strings.items():
            key = data['key']
            replacement = f'Lang.T("{key}")'
            files = "; ".join(data['files'])
            # CSV 转义
            escaped = string.replace('"', '""')
            f.write(f'"{escaped}","{key}","{replacement}","{files}"\n')
    print(f"已生成: {OUTPUT_CSV}")
    
    # 打印替换指南
    print("\n" + "="*60)
    print("替换指南:")
    print("="*60)
    for string, data in list(all_strings.items())[:10]:  # 显示前10个
        print(f'  "{string}"')
        print(f'  → Lang.T("{data["key"]}")')
        print()
    
    if len(all_strings) > 10:
        print(f"  ... 共 {len(all_strings)} 个，详见 {OUTPUT_CSV}")


if __name__ == '__main__':
    main()