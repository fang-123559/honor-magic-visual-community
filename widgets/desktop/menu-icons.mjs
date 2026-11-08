// Small code-native SVGs; independent of optional Windows icon fonts. Native
// Shell bitmap icons, when supplied, take precedence over these fallbacks.
const paths={
 view:'M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h7v7h-7z',
 sort:'M7 3v18m-4-4 4 4 4-4M14 5h7M14 10h5M14 15h3',
 refresh:'M20 10a8 8 0 1 0-1 8M20 3v7h-7',
 new:'M12 4v16M4 12h16',
 'page-next':'M8 5l8 7-8 7',
 'page-previous':'M16 5l-8 7 8 7',
 'page-blocked':'M5.64 5.64l12.72 12.72M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18',
 display:'M4 3h16a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1ZM8 21h8M12 17v4',
 personalize:'m4 16 11-11 4 4L8 20l-5 1zM14 6l4 4',
 more:'M4 12h.01M12 12h.01M20 12h.01',
 app:'M4 3h16a1 1 0 0 1 1 1v16a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1ZM3 8h18M7 5.5h.01M11 5.5h.01',
 paste:'M8 4H5v17h14V4h-3M9 2h6v5H9z',
 'paste-shortcut':'M8 4H5v17h14V4h-3M9 2h6v5H9zM8 16h7m-3-3 3 3-3 3',
 undo:'M8 4 3 9l5 5M3 9h11a6 6 0 0 1 0 12',
 share:'M8 12 17 6M8 12l9 6M5 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6ZM18 2a3 3 0 1 0 0 6 3 3 0 0 0 0-6ZM18 16a3 3 0 1 0 0 6 3 3 0 0 0 0-6Z',
 sendto:'m3 3 18 9-18 9 4-9-4-9Zm4 9h14',
 library:'M4 4h4v17H4ZM10 4h4v17h-4ZM16 5l4-1 3 16-4 1Z',
 restore:'M3 8h7M3 8V2M3 8a9 9 0 1 1 0 9M12 7v6l4 2',
 link:'m9 15 6-6M8 17l-1 1a4 4 0 0 1-6-6l5-5a4 4 0 0 1 6 0M16 7l1-1a4 4 0 0 1 6 6l-5 5a4 4 0 0 1-6 0',
 pin:'m9 3 12 12-4 1-4 4-2-7-7-2 4-4 1-4ZM3 21l7-7',
 edit:'m3 17 13-13 4 4L7 21H3Z',
 print:'M7 8V3h10v5M7 17H3V8h18v9h-4M7 14h10v7H7Z',
 open:'M3 5h7l2 2h9v3H8L3 20V5Zm0 15h15l4-10H8Z',
 cut:'m8 8 12 13M8 16 20 3M5 3a3 3 0 1 0 0 6 3 3 0 0 0 0-6ZM5 15a3 3 0 1 0 0 6 3 3 0 0 0 0-6Z',
 copy:'M8 8h13v13H8ZM16 8V3H3v13h5',
 rename:'M5 5H3v14h2M19 5h2v14h-2M10 3h4M12 3v18M10 21h4M5 10h4M15 10h4',
 delete:'M3 6h18M9 6V3h6v3M6 6l1 15h10l1-15M10 10v7M14 10v7',
 properties:'M12 8h.01M12 11v6M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18',
 document:'M5 3h9l5 5v13H5ZM14 3v6h5M8 13h8M8 17h6',
 'new-folder':'M3 5h7l2 2h9v13H3Z',
 'new-shortcut':'M8 3h10l3 3v15H8ZM3 15h9m-4-4 4 4-4 4',
 'new-text':'M5 3h14v18H5ZM8 7h8M8 11h8M8 15h6',
 runas:'M12 2 3 6v6c0 5 9 10 9 10s9-5 9-10V6ZM12 3v18',
 openas:'M3 3h8v8H3zM14 3h7v8h-7zM3 14h8v7H3zM14 14h7v7h-7z',
 opennewwindow:'M3 3h18v18H3ZM3 7h18M7 7v14',
 'magic-new':'M3 5h7l2 2h9v13H3zM12 10v7M8.5 13.5h7',
 'magic-card':'M3 3h18v18H3ZM3 8h18M12 11v7M8.5 14.5h7',
 reposition:'M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 17h7m-3-3 3 3-3 3',
 feedback:'M4 3h16v14H9l-5 4zM8 7h8M8 11h6',
 exit:'M10 3H3v18h7M8 12h13m-5-5 5 5-5 5'
};
function fallback(document,entry){
  const label=(entry.label||'').replace(/\(&.\)|&/g,'');
  const inferred=[[/^撤销|^Undo/i,'undo'],[/发送到|Send to/i,'sendto'],[/共享|分享|访问权限|Share/i,'share'],[/库中|library/i,'library'],[/还原以前|Previous versions/i,'restore'],[/创建快捷|Create shortcut/i,'link'],[/固定到|Pin to/i,'pin']].find(([pattern])=>pattern.test(label))?.[1];
  const key=paths[entry.key]?entry.key:paths[entry.verb]?entry.verb:inferred||'app';
  const ns='http://www.w3.org/2000/svg',svg=document.createElementNS(ns,'svg'),path=document.createElementNS(ns,'path');
  svg.setAttribute('viewBox','0 0 24 24');svg.setAttribute('aria-hidden','true');svg.setAttribute('fill','none');svg.setAttribute('stroke','currentColor');svg.setAttribute('stroke-width',entry.key==='more'?'3':'1.6');svg.setAttribute('stroke-linecap','round');svg.setAttribute('stroke-linejoin','round');
  path.setAttribute('d',paths[key]);svg.append(path);return svg;
}
export function menuIcon(document,entry){
  if(entry.icon){const image=document.createElement('img');image.alt='';image.className='windows-menu-bitmap';image.addEventListener('error',()=>image.replaceWith(fallback(document,entry)),{once:true});image.src=entry.icon;return image;}
  return fallback(document,entry);
}
