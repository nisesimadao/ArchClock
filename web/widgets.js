/* --------------------------------------------------------------------
   ウィジェットの定義。

   各ウィジェットは次を持つ:
     label    設定画面と配置モードに出す名前
     mount    最初に DOM を組む
     tick     時刻の更新 (時計だけ)
     metrics  ホストから届いた計測値の反映
     hits     クリックできる領域 (ショートカットだけ)

   新しいものを足すときは ArchWidgets に1項目増やすだけでよい。
   -------------------------------------------------------------------- */
window.ArchWidgets = (function(){
  "use strict";

  const p2   = n => String(n).padStart(2, '0');
  const WDJ  = ['日','月','火','水','木','金','土'];
  const esc  = s => String(s).replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
  const opt  = (o, k, d) => (o && o[k] != null) ? o[k] : d;
  const flag = (o, k, d) => { const v = o && o[k]; return v == null ? d : !!v; };

  function isoWeek(d){
    const t = new Date(Date.UTC(d.getFullYear(), d.getMonth(), d.getDate()));
    t.setUTCDate(t.getUTCDate() + 4 - (t.getUTCDay() || 7));
    const y0 = new Date(Date.UTC(t.getUTCFullYear(), 0, 1));
    return Math.ceil(((t - y0) / 86400000 + 1) / 7);
  }
  const dayOfYear = d => Math.floor((d - new Date(d.getFullYear(), 0, 0)) / 86400000);
  const isLeap    = y => (y % 4 === 0 && y % 100 !== 0) || y % 400 === 0;

  function fmtRate(kbps, unit){
    if (unit === 'kb') return [kbps.toFixed(0), 'KB/s'];
    if (unit === 'mb') return [(kbps/1024).toFixed(2), 'MB/s'];
    return kbps >= 1024 ? [(kbps/1024).toFixed(1), 'MB/s'] : [kbps.toFixed(0), 'KB/s'];
  }
  const fmtSize = mb => mb >= 1024 ? (mb/1024).toFixed(1) + ' GB' : Math.round(mb) + ' MB';

  /** どのウィジェットにも共通の見た目。 */
  function applyCommon(el, o, d){
    d = d || {};
    const align = opt(o, 'align', d.align || 'left');
    el.style.textAlign = align;
    el.dataset.align   = align;

    const width = opt(o, 'width', d.width);
    el.style.width = width ? width + 'px' : '';

    el.style.opacity   = (opt(o, 'opacity', 100) / 100);
    el.style.lineHeight = opt(o, 'lineHeight', 0) ? opt(o, 'lineHeight', 150) / 100 : '';
  }

  /** 見出し。出す/出さない と文字を差し替えられる。 */
  function head(o, defLabel, right){
    if (!flag(o, 'showHeader', true)) return '';
    const label = esc(opt(o, 'label', defLabel));
    return `<div class="head"><span>${label}</span>${right || ''}</div>`;
  }

  /** しきい値を超えたら色を変える。 */
  function warnColor(el, value, o){
    const at = opt(o, 'warnAt', 0);
    if (!at || value < at){ el.style.removeProperty('--accent'); return; }
    el.style.setProperty('--accent', opt(o, 'warnColor', '#ff6b6b'));
  }

  // ================================================================
  return {

    // ---------------- 時計 ----------------
    clock: {
      label: '時計',
      mount(el, o){
        applyCommon(el, o, { align:'right' });
        el.style.setProperty('--w', opt(o, 'weight', 250));
        el.innerHTML = `
          <div class="time" style="font-size:${opt(o,'size',176)}px">
            <span class="hh">--</span><span class="c">:</span><span class="mm">--</span><span class="s"></span>
          </div>
          <div class="date" style="font-size:${opt(o,'dateSize',30)}px"></div>
          <div class="meta" style="font-size:${opt(o,'metaSize',19)}px"></div>`;
        el.querySelector('.s').style.display    = flag(o,'showSeconds',true) ? '' : 'none';
        el.querySelector('.date').style.display = flag(o,'showDate',true)    ? '' : 'none';
        el.querySelector('.meta').style.display = flag(o,'showMeta',true)    ? '' : 'none';
      },
      tick(el, d, o){
        const h24 = flag(o,'hour24',true);
        let h = d.getHours();
        if (!h24) h = h % 12 || 12;

        el.querySelector('.hh').textContent = h24 ? p2(h) : String(h);
        el.querySelector('.mm').textContent = p2(d.getMinutes());
        if (flag(o,'showSeconds',true)) el.querySelector('.s').textContent = p2(d.getSeconds());

        if (flag(o,'showDate',true)){
          const style = opt(o,'dateStyle','jp');
          const wd = WDJ[d.getDay()];
          el.querySelector('.date').textContent =
            style === 'slash' ? `${d.getFullYear()}/${p2(d.getMonth()+1)}/${p2(d.getDate())} (${wd})`
          : style === 'short' ? `${d.getMonth()+1}月${d.getDate()}日 (${wd})`
          :                     `${d.getFullYear()}年${d.getMonth()+1}月${d.getDate()}日 (${wd})`;
        }

        if (flag(o,'showMeta',true)){
          const doy = dayOfYear(d), total = isLeap(d.getFullYear()) ? 366 : 365;
          const parts = [];
          if (flag(o,'metaWeek',true))      parts.push(`第${isoWeek(d)}週`);
          if (flag(o,'metaDayOfYear',true)) parts.push(`${doy} / ${total} 日`);
          if (flag(o,'metaRemain',true))    parts.push(`残り ${total-doy} 日`);
          el.querySelector('.meta').innerHTML = parts.map(p => `<span>${p}</span>`).join('');
        }
      },
    },

    // ---------------- CPU ----------------
    cpu: {
      label: 'CPU 使用率',
      mount(el, o){
        el.classList.add('gauge');
        applyCommon(el, o, { width:300 });
        el.innerHTML =
          head(o, 'CPU', `<span class="val" style="font-size:${opt(o,'valueSize',38)}px">--</span>`)
          + (flag(o,'showBar',true) ? `<div class="bar"><i></i></div>` : '')
          + `<div class="sub" style="font-size:${opt(o,'subSize',16)}px"></div>`;
        if (!flag(o,'showHeader',true))
          el.insertAdjacentHTML('afterbegin',
            `<div class="val" style="font-size:${opt(o,'valueSize',38)}px">--</div>`);
      },
      metrics(el, m, o){
        const v = m.cpuPercent ?? 0;
        const dec = opt(o,'decimals',0);
        const val = el.querySelector('.val');
        if (val) val.innerHTML =
          `${v.toFixed(dec)}<small style="font-size:.5em;opacity:.6">%</small>`;

        const bar = el.querySelector('.bar > i');
        if (bar) bar.style.width = Math.min(100, v) + '%';
        warnColor(el, v, o);

        const sub = el.querySelector('.sub');
        if (!sub) return;
        const n = opt(o,'topCount', flag(o,'showTop',true) ? 1 : 0);
        if (!n){ sub.textContent = ''; return; }
        sub.innerHTML = (m.topByCpu || []).slice(0, n)
          .map(t => `<div>${esc(t.name)} ${t.cpu.toFixed(1)}%</div>`).join('');
      },
    },

    // ---------------- メモリ ----------------
    memory: {
      label: 'メモリ使用率',
      mount(el, o){
        el.classList.add('gauge');
        applyCommon(el, o, { width:300 });
        el.innerHTML =
          head(o, 'MEMORY', `<span class="val" style="font-size:${opt(o,'valueSize',38)}px">--</span>`)
          + (flag(o,'showBar',true) ? `<div class="bar"><i></i></div>` : '')
          + `<div class="sub" style="font-size:${opt(o,'subSize',16)}px"></div>`;
        if (!flag(o,'showHeader',true))
          el.insertAdjacentHTML('afterbegin',
            `<div class="val" style="font-size:${opt(o,'valueSize',38)}px">--</div>`);
      },
      metrics(el, m, o){
        const mem = m.memory || {};
        const pct = mem.percent ?? 0, used = mem.usedGb ?? 0, total = mem.totalGb ?? 0;
        const dec = opt(o,'decimals',0);
        const mode = opt(o,'mode','percent');

        const val = el.querySelector('.val');
        if (val){
          val.innerHTML = mode === 'used'
            ? `${used.toFixed(1)}<small style="font-size:.5em;opacity:.6"> GB</small>`
            : mode === 'free'
            ? `${(total-used).toFixed(1)}<small style="font-size:.5em;opacity:.6"> GB</small>`
            : `${pct.toFixed(dec)}<small style="font-size:.5em;opacity:.6">%</small>`;
        }
        const bar = el.querySelector('.bar > i');
        if (bar) bar.style.width = Math.min(100, pct) + '%';
        warnColor(el, pct, o);

        const sub = el.querySelector('.sub');
        if (!sub) return;
        sub.textContent = !flag(o,'showSub',true) ? ''
          : mode === 'free' ? `${used.toFixed(1)} GB 使用中`
          : `${used.toFixed(1)} / ${total.toFixed(1)} GB`;
      },
    },

    // ---------------- プロセス一覧 ----------------
    processes: {
      label: 'プロセス一覧',
      mount(el, o){
        el.classList.add('procs');
        applyCommon(el, o, { width:420 });
        const by = opt(o,'sortBy','cpu') === 'memory' ? 'メモリ順' : 'CPU順';
        el.innerHTML =
          head(o, 'PROCESSES', flag(o,'showSortLabel',true) ? `<span class="by">${by}</span>` : '')
          + `<table style="font-size:${opt(o,'rowSize',18)}px"><tbody></tbody></table>`;
      },
      metrics(el, m, o){
        const byMem = opt(o,'sortBy','cpu') === 'memory';
        const list  = (byMem ? m.topByMemory : m.topByCpu) || [];
        const n     = opt(o,'count',6);
        const gap   = opt(o,'rowGap',5);

        const showCpu = flag(o,'colCpu',true);
        const showMem = flag(o,'colMem',true);
        const showPid = flag(o,'colPid',false);

        el.querySelector('tbody').innerHTML = list.slice(0, n).map((p,i) => `
          <tr class="${i===0 ? 'top' : ''}">
            <td class="name" style="padding:${gap}px 16px ${gap}px 0">${esc(p.name)}</td>
            ${showPid ? `<td class="num" style="padding:${gap}px 0">${p.pid}</td>` : ''}
            ${showCpu ? `<td class="num" style="padding:${gap}px 0">${p.cpu.toFixed(1)}%</td>` : ''}
            ${showMem ? `<td class="num" style="padding:${gap}px 0">${fmtSize(p.memMb)}</td>` : ''}
          </tr>`).join('');
      },
    },

    // ---------------- ディスク ----------------
    disk: {
      label: 'ディスク空き',
      mount(el, o){
        el.classList.add('disks');
        applyCommon(el, o, { width:360 });
        el.innerHTML = head(o, 'DISK') + `<div class="rows" style="font-size:${opt(o,'rowSize',17)}px"></div>`;
      },
      metrics(el, m, o){
        const only = String(opt(o,'drives','')).trim();
        const keep = only ? only.toUpperCase().split(/[,\s]+/).filter(Boolean) : null;
        const mode = opt(o,'mode','free');
        const bar  = flag(o,'showBar',true);

        const rows = (m.disks || []).filter(d => !keep || keep.includes(d.name.replace(':','').toUpperCase()));
        el.querySelector('.rows').innerHTML = rows.map(d => {
          const text = mode === 'used'    ? `${d.usedGb.toFixed(0)} GB 使用`
                     : mode === 'percent' ? `${d.percent.toFixed(0)}%`
                     : mode === 'both'    ? `${(d.totalGb-d.usedGb).toFixed(0)} / ${d.totalGb.toFixed(0)} GB`
                     :                      `${(d.totalGb-d.usedGb).toFixed(0)} GB 空き`;
          return `<div class="row">
            <span class="name">${esc(d.name)}</span>
            ${bar ? `<span class="bar"><i style="width:${Math.min(100,d.percent)}%"></i></span>` : ''}
            <span class="num">${text}</span>
          </div>`;
        }).join('');
      },
    },

    // ---------------- ネットワーク ----------------
    network: {
      label: 'ネットワーク',
      mount(el, o){
        el.classList.add('net');
        applyCommon(el, o, { width:240 });
        const size = opt(o,'rowSize',22);
        el.innerHTML = head(o, 'NETWORK')
          + (flag(o,'showDown',true) ? `<div class="row down" style="font-size:${size}px"><span class="k">${esc(opt(o,'labelDown','DOWN'))}</span><span class="v">--</span><span class="u"></span></div>` : '')
          + (flag(o,'showUp',true)   ? `<div class="row up"   style="font-size:${size}px"><span class="k">${esc(opt(o,'labelUp','UP'))}</span><span class="v">--</span><span class="u"></span></div>` : '');
      },
      metrics(el, m, o){
        const n = m.network || {};
        const unit = opt(o,'unit','auto');
        const set = (sel, kbps) => {
          const row = el.querySelector(sel);
          if (!row) return;
          const [v,u] = fmtRate(kbps ?? 0, unit);
          row.querySelector('.v').textContent = v;
          row.querySelector('.u').textContent = flag(o,'showUnit',true) ? u : '';
        };
        set('.row.down', n.downKbps);
        set('.row.up',   n.upKbps);
      },
    },

    // ---------------- ショートカット ----------------
    shortcuts: {
      label: 'アプリのショートカット',
      mount(el, o){
        el.classList.add('shortcuts');
        const icon = opt(o,'iconSize',52), gap = opt(o,'gap',22);
        el.style.setProperty('--icon', icon + 'px');
        el.style.setProperty('--cell', (icon + 24) + 'px');
        el.style.setProperty('--gap',  gap + 'px');
        el.style.setProperty('--radius', opt(o,'radius',12) + 'px');
        applyCommon(el, o, { width: opt(o,'columns',0) ? opt(o,'columns',6) * (icon + 24 + gap) : 0 });

        // アイコンの URL は設定に残さず、表示のたびにホストへ要求する。
        // 保存済みの URL に頼ると、キャッシュが作り直されたときに画像切れになる。
        const items = o.items || [];
        const vertical = opt(o,'direction','row') === 'column';
        el.innerHTML = `<div class="grid" style="flex-direction:${vertical ? 'column' : 'row'}">`
          + items.map((it,i) => `
          <div class="item" data-i="${i}" data-path="${esc(it.path || '')}" title="${esc(it.name || '')}">
            <span class="ripple"></span>
            <img class="ic" alt=""${it.icon ? ` src="${esc(it.icon)}"` : ''}>
            ${flag(o,'showLabels',true) ? `<span style="font-size:${opt(o,'labelSize',13)}px">${esc(it.name || '')}</span>` : ''}
          </div>`).join('') + `</div>`;
      },
      hits(el, widget){
        const items = (widget.options && widget.options.items) || [];
        const out = [];
        el.querySelectorAll('.item').forEach(node => {
          const it = items[+node.dataset.i];
          if (!it || !it.path) return;
          const r = node.getBoundingClientRect();
          out.push({ id: widget.id, x:r.left, y:r.top, w:r.width, h:r.height, target: it.path });
        });
        return out;
      },
    },

  };
})();
