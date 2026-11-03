// 背景のレイアウトを ?bg= で切り替え、右端の列（レイアウト切替＋新しいスピーカー）を描く
const bg = new URLSearchParams(location.search).get('bg') || 'aurora-night';
document.body.style.backgroundImage = `url(../docs/images/${bg}.png)`;
const names = {classic:'標準', gauge:'計器盤', 'aurora-night':'オーロラ'};
document.querySelectorAll('.lay').forEach(e => e.textContent = names[bg]);
const spk = '<svg class="ic" width="26" height="26" viewBox="0 0 24 24"><path d="M4 9v6h4l5 4V5L8 9z"/><path d="M16 9a4 4 0 0 1 0 6"/><path d="M18.5 6.5a8 8 0 0 1 0 11"/></svg>';
let html = '';
if (bg === 'classic') // 標準は普段隠れている列を、マウスが乗っている状態で描く
  html += `<div class="rail glass" style="top:318px">
    <div class="on"><svg class="ic" width="26" height="26" viewBox="0 0 24 24"><rect x="4" y="4" width="16" height="16" rx="2"/><path d="M12 4v16M4 12h16"/></svg></div>
    <div><svg class="ic" width="26" height="26" viewBox="0 0 24 24"><path d="M5 16a7 7 0 0 1 14 0"/><path d="M12 16l3-4"/></svg></div>
    <div><svg class="ic" width="26" height="26" viewBox="0 0 24 24"><path d="M4 15c3-5 5 1 8-3s5-1 8-4"/><path d="M4 19h16"/></svg></div></div>`;
html += `<div class="rail glass" style="top:506px"><div class="on">${spk}</div></div>`;
document.body.insertAdjacentHTML('beforeend', html);
