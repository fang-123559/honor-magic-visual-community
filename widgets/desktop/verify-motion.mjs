import assert from "node:assert/strict";
import { folderMotion } from "./folder-motion.mjs";
const runs = [];
function fixture(reduced = false) {
  const animations=[];
  const dialog={open:false,showModal(){this.open=true;},close(){this.open=false;},animate(frames,options){
    let resolve,reject;const finished=new Promise((yes,no)=>{resolve=yes;reject=no;});
    const animation={frames,options,finished,cancel(){reject(new Error("cancelled"));},finish:resolve};animations.push(animation);return animation;
  }};
  return {dialog,animations,motion:folderMotion(dialog,()=>reduced)};
}
{
  const {dialog,animations,motion}=fixture();motion.open();assert.equal(dialog.open,true);assert.equal(animations[0].options.duration,180);
  const closed=motion.close();assert.equal(dialog.open,true);assert.equal(animations[1].options.duration,140);animations[1].finish();await closed;assert.equal(dialog.open,false);
  assert.ok(animations.every(a=>a.frames.every(frame=>Object.keys(frame).every(key=>["opacity","transform"].includes(key)))));runs.push("bounded-opacity-transform-open-close");
}
{
  const {dialog,animations,motion}=fixture();motion.open();const closing=motion.close();motion.open();await closing;assert.equal(dialog.open,true);animations.at(-1).finish();runs.push("reopen-cancels-pending-close");
}
{
  const {dialog,animations,motion}=fixture(true);motion.open();await motion.close();assert.equal(dialog.open,false);assert.equal(animations.length,0);runs.push("reduced-motion-has-no-animation");
}
{
  const {dialog,motion}=fixture();motion.open();await motion.close(true);assert.equal(dialog.open,false);await motion.close();runs.push("drag-close-immediate-and-close-idempotent");
}
console.log(JSON.stringify({allPass:true,checks:runs,scope:"Animation lifecycle unit checks; visual frame pacing requires real desktop acceptance"}));
