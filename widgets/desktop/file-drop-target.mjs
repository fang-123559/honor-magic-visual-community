// Physical desktop folders and Recycle Bin take priority over grid placement.
// A large Magic folder is handled separately as virtual grouping.
export function fileDropTarget(sourceId,target){
  if(!target||target.id===sourceId)return null;
  if(target.isRecycleBin)return {action:'recycle',targetId:target.id};
  if(target.isFolder&&!target.isVirtual)return {action:'move',targetId:target.id};
  return null;
}
