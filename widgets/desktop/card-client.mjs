export function createDesktopCardClient({request,monitorId,revision,changed=()=>{}}){
 const act=async(kind,values)=>{const result=await request('/desktop-card-action',{...values,monitorId,revision:revision(),kind},0);await changed();return result;};
 return {
  catalog:()=>request('/desktop-cards/catalog'),
  list:()=>request('/desktop-cards/manage',{monitorId,kind:'list'}),
  openDirectory:()=>request('/desktop-cards/manage',{monitorId,kind:'open-directory'}),
  uninstall:async(type,fingerprint,instances,expectedRevision)=>{const result=await request('/desktop-cards/manage',{monitorId,kind:'uninstall',type,fingerprint,instances,revision:expectedRevision},0);await changed();return result;},
  create:(type,columns,rows,settings={},position={})=>act('create',{type,columns,rows,settings,...position}),
  configure:(id,settings)=>act('configure',{id,settings}),resize:(id,columns,rows)=>act('resize',{id,columns,rows}),
  remove:id=>act('remove',{id}),place:(id,x,y)=>act('place',{id,x,y}),
  moveScreen:(id,boardId,position={})=>act('move-screen',{id,boardId,...position})
 };
}
