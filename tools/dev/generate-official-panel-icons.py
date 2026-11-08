"""Generate embedded HONOR panel SVGs from ordinary files in this checkout."""
from pathlib import Path
import re
root=Path(__file__).resolve().parents[2]
source=root/'assets/native-official-panels'
items=[]
for p in sorted(source.glob('*.svg')):
    svg=p.read_text(encoding='utf-8-sig').strip()
    svg=re.sub(r'(fill|stroke)="#[0-9a-fA-F]{6}"',r'\1="currentColor"',svg)
    svg=re.sub(r'width="[^"]+"','width="24"',svg,count=1)
    svg=re.sub(r'height="[^"]+"','height="24"',svg,count=1)
    items.append('    {L"'+p.stem+'",R"official('+svg+')official"},')
header='#pragma once\n// HONOR originals; see assets/native-official-panels/NOTICE.md.\nstruct OfficialPanelAsset{wchar_t const *name;char const *svg;};\ninline constexpr OfficialPanelAsset OfficialPanelAssets[]={\n'+'\n'.join(items)+'\n};\n'
(root/'src/composition-trial/native-official-panel-icons.h').write_bytes(header.encode('utf-8'))
print('Embedded',len(items),'official panel icons')
