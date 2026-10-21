/* ============================================================
   疯狂动物麻将 · 千术麻将 UI 原型
   ------------------------------------------------------------
   纯原生 JavaScript，无 CDN / 无构建 / 无网络请求。
   美术素材（本地 PNG，仅作装饰层与角色肖像）：
     assets/club-background.png  麻将馆场景（无文字）
     assets/animal-roster.png     3×2 肖像：章鱼/喜鹊/狐狸 · 猴子/企鹅/熊猫
     assets/animal-extra.png     2×1 肖像：老虎/乌鸦
   全部房间、牌局、角色与结算数据均为「演示模拟数据」，
   本脚本不发起任何网络请求，也不代表真实游戏已实现该功能。
   ============================================================ */
(function () {
  'use strict';

  /* ==========================================================
     0. 基础常量：麻将牌
     ========================================================== */

  const CN_NUM = ['', '一', '二', '三', '四', '五', '六', '七', '八', '九'];
  const HONOR_INFO = {
    z1: { ch: '東', name: '东', cls: '' },
    z2: { ch: '南', name: '南', cls: '' },
    z3: { ch: '西', name: '西', cls: '' },
    z4: { ch: '北', name: '北', cls: '' },
    z5: { ch: '中', name: '中', cls: 'tile__honor--red' },
    z6: { ch: '發', name: '发', cls: 'tile__honor--green' },
    z7: { ch: '白', name: '白', cls: 'tile__honor--frame' }
  };

  // 3x3 宫格索引：0 1 2 / 3 4 5 / 6 7 8
  const PIN_LAYOUT = {
    1: [4], 2: [0, 8], 3: [0, 4, 8], 4: [0, 2, 6, 8], 5: [0, 2, 4, 6, 8],
    6: [0, 2, 3, 5, 6, 8], 7: [0, 2, 3, 4, 5, 6, 8], 8: [0, 1, 2, 3, 5, 6, 7, 8],
    9: [0, 1, 2, 3, 4, 5, 6, 7, 8]
  };
  const BAM_LAYOUT = {
    1: [4], 2: [1, 7], 3: [0, 4, 8], 4: [0, 2, 6, 8], 5: [0, 2, 4, 6, 8],
    6: [0, 2, 3, 5, 6, 8], 7: [0, 2, 3, 4, 5, 6, 8], 8: [0, 1, 2, 3, 5, 6, 7, 8],
    9: [0, 1, 2, 3, 4, 5, 6, 7, 8]
  };

  const BIRD_SVG =
    '<svg viewBox="0 0 40 56" aria-hidden="true" focusable="false">' +
    '<path d="M20 5c8 3 11 9 10 15-2 6-7 9-10 10-3-1-8-4-10-10-1-6 2-12 10-15z" fill="#c94a3a"/>' +
    '<circle cx="20" cy="24" r="8" fill="#3f9a52"/>' +
    '<path d="M27 23l9 3-9 3z" fill="#c94a3a"/>' +
    '<circle cx="17" cy="22" r="1.9" fill="#12251a"/>' +
    '<path d="M11 30c-4 5-4 13-1 19h20c3-6 3-14-1-19-4 5-14 5-18 0z" fill="#2f7a42"/>' +
    '<path d="M12 44h6v8h-6zM22 44h6v8h-6z" fill="#c94a3a"/>' +
    '</svg>';

  function tileInfo(code) {
    const suit = code.charAt(0);
    const rank = parseInt(code.slice(1), 10);
    if (suit === 'm') return { name: CN_NUM[rank] + '万', suit: '万', rank: rank };
    if (suit === 'p') return { name: rank + '筒', suit: '筒', rank: rank };
    if (suit === 's') return { name: rank + '条', suit: '条', rank: rank };
    const h = HONOR_INFO[code];
    return { name: h.name, suit: '字', rank: code.slice(1), ch: h.ch, cls: h.cls };
  }

  function tileFaceHTML(code) {
    const suit = code.charAt(0);
    const rank = parseInt(code.slice(1), 10);
    if (suit === 'm') {
      return '<span class="tile__body"><span class="tile__man-num">' + CN_NUM[rank] +
        '</span><span class="tile__man-char">万</span></span>';
    }
    if (suit === 'p') {
      const cells = PIN_LAYOUT[rank] || [];
      let out = '';
      for (let i = 0; i < 9; i++) out += cells.indexOf(i) >= 0 ? '<i class="pip-dot"></i>' : '<i></i>';
      return '<span class="tile__body"><span class="pips' + (rank === 1 ? ' pips--big' : '') +
        '">' + out + '</span></span>';
    }
    if (suit === 's') {
      if (rank === 1) return '<span class="tile__body tile__body--bird">' + BIRD_SVG + '</span>';
      const cells = BAM_LAYOUT[rank] || [];
      let out = '';
      for (let i = 0; i < 9; i++) {
        if (cells.indexOf(i) < 0) { out += '<i></i>'; continue; }
        out += '<i class="bamboo' + (rank === 5 && i === 4 ? ' bamboo--red' : '') + '"></i>';
      }
      return '<span class="tile__body"><span class="bamboos">' + out + '</span></span>';
    }
    const h = HONOR_INFO[code];
    return '<span class="tile__body"><span class="tile__honor ' + h.cls + '">' + h.ch + '</span></span>';
  }

  function buildWall() {
    const wall = [];
    'mp s'.replace(' ', '').split('').forEach(function (suit) {
      for (let i = 1; i <= 9; i++) {
        wall.push(suit + i, suit + i, suit + i, suit + i);
      }
    });
    for (let i = 1; i <= 7; i++) wall.push('z' + i, 'z' + i, 'z' + i, 'z' + i);
    return wall; // 136 张，物理守恒
  }

  function shuffle(arr, seed) {
    const a = arr.slice();
    let s = seed >>> 0;
    function rnd() {
      s = (s + 0x6D2B79F5) >>> 0;
      let t = s;
      t = Math.imul(t ^ (t >>> 15), t | 1);
      t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    }
    for (let i = a.length - 1; i > 0; i--) {
      const j = Math.floor(rnd() * (i + 1));
      const tmp = a[i]; a[i] = a[j]; a[j] = tmp;
    }
    return a;
  }

  const SORT_ORDER = { m: 0, p: 1, s: 2, z: 3 };
  // 手牌 / 牌山中的每一张牌都是物理实体：{ c: 牌面代码, u: 实体唯一编号 }
  // 落汗标记按实体编号追踪，因此同名牌也不会互相串味。
  function sortHand(list) {
    return list.slice().sort(function (a, b) {
      const sa = SORT_ORDER[a.c.charAt(0)], sb = SORT_ORDER[b.c.charAt(0)];
      if (sa !== sb) return sa - sb;
      return a.c.localeCompare(b.c);
    });
  }

  /* ==========================================================
     1. 动物角色（只加强三大绝技或改变费用，无独立新技能）
     ========================================================== */

  /* 角色肖像来源（本地 PNG 精灵图，由 CSS 裁切显示）：
     roster: 3 列 × 2 行 —— 章鱼 喜鹊 狐狸 / 猴子 企鹅 熊猫
     extra:  2 列 × 1 行 —— 老虎 乌鸦
     肖像只作视觉层，角色的名称、绝技与特性全部由 DOM 文本提供。 */
  const PORTRAIT_SHEET = {
    octopus: 'roster', magpie: 'roster', fox: 'roster',
    monkey: 'roster', penguin: 'roster', panda: 'roster',
    tiger: 'extra', crow: 'extra'
  };


  const CHARACTERS = [
    {
      id: 'octopus', name: '章鱼', en: 'OCTOPUS', trait: '三腕藏花',
      desc: '使用袖里藏花时，有 20% 概率看到三张候选牌；但最终仍然只保留一张，其余物理牌严格守恒。',
      affects: '袖里藏花 · 20% 概率候选变为三张（仍然 3 选 1）',
      notes: ['不增加手牌数量，未选中的候选返回牌山', '只在袖里藏花生效，其余两招不受影响']
    },
    {
      id: 'magpie', name: '喜鹊', en: 'MAGPIE', trait: '第一滴汗',
      desc: '每局第一次使用落汗时，出千点费用减少 4 点。',
      affects: '落汗 · 每局首次施术 10 → 6 点',
      notes: ['每局仅一次，第二次起恢复 10 点', '不改变落汗的标记张数与效果']
    },
    {
      id: 'fox', name: '狐狸', en: 'FOX', trait: '偷看一眼',
      desc: '偷梁换柱时可以额外预览一个合法候选槽位，看到槽位中暗置的牌面。',
      affects: '偷梁换柱 · 候选槽位 3 → 4（额外预览 1 个）',
      notes: ['只增加可选槽位，不增加交换数量', '交换仍为双方各一张，物理守恒']
    },
    {
      id: 'monkey', name: '猴子', en: 'MONKEY', trait: '换手',
      desc: '同一局中第一次使用「与上一招不同」的绝技时，出千点费用减少 3 点。',
      affects: '落汗 / 袖里藏花 / 偷梁换柱 · 同局第二种不同绝技 -3 点',
      notes: ['同一种绝技重复施术不再减免', '每局最多触发一次']
    },
    {
      id: 'penguin', name: '企鹅', en: 'PENGUIN', trait: '留一手',
      desc: '袖里藏花时若选择保留原来的那张摸牌，返还 3 点出千点。',
      affects: '袖里藏花 · 保留原摸牌时返还 3 点',
      notes: ['只在选择保留原摸牌时返还', '不影响候选数量与守恒规则']
    },
    {
      id: 'panda', name: '熊猫', en: 'PANDA', trait: '一好一坏',
      desc: '当局第一招绝技费用减半，当局第二招绝技费用增加 3 点。',
      affects: '三大绝技 · 当局第一招半价，第二招 +3 点',
      notes: ['按当局施术顺序计算，与绝技种类无关', '第一招之后每招都 +3 点']
    },
    {
      id: 'tiger', name: '老虎', en: 'TIGER', trait: '额外一组',
      desc: '落汗每局可维持的标记上限增加一组（4 张）。',
      affects: '落汗 · 标记上限 12 → 16 张',
      notes: ['只提高标记张数上限', '每张仍消耗 1 个标记位，不影响费用']
    },
    {
      id: 'crow', name: '乌鸦', en: 'CROW', trait: '折价',
      desc: '偷梁换柱的出千点费用减少 2 点。',
      affects: '偷梁换柱 · 10 → 8 点',
      notes: ['固定减价，不随其它折扣叠加计算', '不影响候选数量']
    }
  ];

  const CHAR_BY_ID = {};
  CHARACTERS.forEach(function (c) { CHAR_BY_ID[c.id] = c; });

  // 角色关联的绝技（只用于卡片上的图标与标签，不改变任何规则）
  const CHAR_CHEAT = {
    octopus: 'sleeveBloom', magpie: 'tileMark', fox: 'beamSwap',
    monkey: 'tileMark', penguin: 'sleeveBloom', panda: 'beamSwap',
    tiger: 'tileMark', crow: 'beamSwap'
  };

  function charCheatLabel(charId) {
    if (charId === 'monkey' || charId === 'panda') return { glyph: '术', name: '三绝技' };
    const id = CHAR_CHEAT[charId];
    const c = id ? CHEAT_BY_ID[id] : null;
    return c ? { glyph: c.glyph, name: c.name } : { glyph: '术', name: '三绝技' };
  }

  /* ==========================================================
     2. 三大绝技定义
     ========================================================== */

  const CHEATS = [
    {
      id: 'tileMark', name: '落汗', en: 'TILE_MARK', glyph: '汗', baseCost: 10,
      desc: '在手牌中选择 4 张实体牌留下微痕标记。标记牌进入对手手牌时可识别牌面，打入牌河时精准定位。',
      notes: ['每局最多同时维持 12 张标记牌', '标记在对手手中仍可辨认牌面', '当局立即生效，另持续后续 3 局'],
      warn: '思考超时仅禁止新施术，已施费用不退。'
    },
    {
      id: 'sleeveBloom', name: '袖里藏花', en: 'SLEEVE_BLOOM', glyph: '花', baseCost: 10,
      desc: '摸牌后消耗出千点，从候选中保留一张，其余候选返回牌山。',
      notes: ['默认二选一保留', '立直后仍可施术', '他家回合可预定，待本人下次普通摸牌入手'],
      warn: '超时或预定后不退款。'
    },
    {
      id: 'beamSwap', name: '偷梁换柱', en: 'BEAM_SWAP', glyph: '换', baseCost: 10,
      desc: '摸牌后出牌前消耗出千点，从牌山暗槽选一张与手牌等额互换。',
      notes: ['双方各一张，牌总数严格守恒', '候选暗槽默认不露牌面', '立直后禁止施术'],
      warn: '超时摸切费用不退。'
    }
  ];
  const CHEAT_BY_ID = {};
  CHEATS.forEach(function (c) { CHEAT_BY_ID[c.id] = c; });

  const THEMES = [
    { id: 'classic', name: '经典象牙' },
    { id: 'neon', name: '夜市霓虹' },
    { id: 'beast', name: '兽纹墨玉' }
  ];

  // 动效强度：auto = 跟随系统 prefers-reduced-motion；full / reduced = 用户显式指定
  const MOTION_MODES = [
    { id: 'auto', name: '跟随系统' },
    { id: 'full', name: '完整动效' },
    { id: 'reduced', name: '减弱动效' }
  ];

  const DEMO_ROOMS = [
    { id: 'MJ-7K2Q', mode: '东风战', players: 3, cap: 4, host: '月读', state: '等待中' },
    { id: 'MJ-3D8X', mode: '半庄战', players: 2, cap: 4, host: '狸猫', state: '等待中' },
    { id: 'MJ-9F1P', mode: '东风战', players: 4, cap: 4, host: '白鹭', state: '进行中' }
  ];
  const DEMO_NAMES = ['月读', '狸猫', '白鹭', '青行灯', '赤鬼', '猫又'];
  const DEFAULT_STACK_SLOTS = [
    { code: 'm1', label: '一萬' }, { code: 'p5', label: '五筒' }, { code: 's9', label: '九索' },
    { code: 'z5', label: '紅中' }, { code: 'z1', label: '東' }, { code: 'p9', label: '九筒' }
  ];

  /* ==========================================================
     3. 全局状态与本地存档
     ========================================================== */

  const STORAGE_KEY = 'mj-animal-demo/session-v2';

  const state = {
    screen: 'menu',
    navStack: [],
    mode: null,
    characterId: 'octopus',
    locked: {},
    settings: { theme: 'classic', motion: 'auto', textsize: 'md', octopusDemo: false },
    room: null,
    round: null,
    ledger: []
  };

  function saveSession() {
    try {
      const payload = {
        characterId: state.characterId,
        locked: state.locked,
        settings: state.settings,
        mode: state.mode,
        room: state.room,
        round: state.round,
        ledger: state.ledger
      };
      localStorage.setItem(STORAGE_KEY, JSON.stringify(payload));
    } catch (err) { /* 演示环境可能禁用 localStorage，忽略即可 */ }
  }

  function loadSession() {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return;
      const data = JSON.parse(raw);
      if (data && typeof data === 'object') {
        if (data.characterId && CHAR_BY_ID[data.characterId]) state.characterId = data.characterId;
        if (data.locked && typeof data.locked === 'object') state.locked = data.locked;
        if (data.settings) {
          Object.keys(state.settings).forEach(function (k) {
            if (data.settings[k] !== undefined) state.settings[k] = data.settings[k];
          });
        }
        if (data.mode) state.mode = data.mode;
        if (data.room) state.room = data.room;
        if (data.round) state.round = data.round;
        if (Array.isArray(data.ledger)) state.ledger = data.ledger;
      }
    } catch (err) { /* 忽略损坏的存档 */ }
  }

  /* ==========================================================
     4. 工具函数
     ========================================================== */

  const $ = function (sel, root) { return (root || document).querySelector(sel); };
  const $$ = function (sel, root) {
    return Array.prototype.slice.call((root || document).querySelectorAll(sel));
  };

  function esc(str) {
    return String(str).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }

  function announce(msg) {
    const live = $('#live-region');
    if (!live) return;
    live.textContent = '';
    window.setTimeout(function () { live.textContent = msg; }, 30);
  }

  function toast(msg, kind) {
    const stack = $('#toast-stack');
    if (!stack) return;
    const el = document.createElement('div');
    el.className = 'toast' + (kind ? ' toast--' + kind : '');
    el.textContent = msg;
    stack.appendChild(el);
    window.setTimeout(function () {
      el.classList.add('is-out');
      window.setTimeout(function () { if (el.parentNode) el.parentNode.removeChild(el); }, 320);
    }, 3000);
  }

  /**
   * 角色肖像：使用本地 PNG 精灵图（assets/animal-roster.png / animal-extra.png），
   * 由 CSS 背景定位裁切。默认使用「头部特写」裁切（portrait--face），
   * 该裁切已逐角色验证不会越出各自的图格；完整立绘用 portrait--full。
   * 肖像只作装饰层，名称 / 绝技 / 特性一律由 DOM 文本提供。
   */
  function portraitHTML(charId, opts) {
    opts = opts || {};
    const c = CHAR_BY_ID[charId];
    if (!c) return '';
    const face = opts.face === false ? '' : ' portrait--face';
    const extra = opts.cls ? ' ' + opts.cls : '';
    const a11y = opts.label
      ? ' role="img" aria-label="' + esc(opts.label) + '"'
      : ' aria-hidden="true"';
    return '<span class="portrait portrait--' + c.id + face + extra + '"' + a11y + '></span>';
  }

  /**
   * 费用预览条。
   * 只有费用真的发生变化（角色折扣）时才显示「原价 → 实付」；
   * 没有变化时只显示一次金额，避免出现「10  10 点」这种重复读数。
   * 企鹅返还等额外说明以附注形式给出。
   */
  function costPreviewHTML(priced, opts) {
    opts = opts || {};
    const refund = opts.refund || 0;
    const net = Math.max(0, priced.cost - refund);
    const changed = priced.cost !== priced.base;
    let html = '<div class="cost-preview"><span class="cost-preview__label">本次费用</span>';
    if (changed) {
      html += '<span class="cost-preview__base">原价 ' + priced.base + ' 点</span>' +
        '<span class="cost-preview__arrow" aria-hidden="true">→</span>' +
        '<span class="cost-preview__now">实付 ' + net + ' 点</span>';
    } else {
      html += '<span class="cost-preview__now">' + priced.cost + ' 点</span>';
      if (refund) {
        html += '<span class="cost-preview__note">· 企鹅保留原摸牌返还 ' + refund + ' 点，实付 ' + net + ' 点</span>';
      }
    }
    if (priced.notes && priced.notes.length) {
      html += '<span class="cost-preview__notes">' + priced.notes.map(function (n) {
        return '<span class="cost-preview__note">· ' + esc(n) + '</span>';
      }).join('') + '</span>';
    }
    if (opts.extra) html += '<span class="cost-preview__extra">' + opts.extra + '</span>';
    return html + '</div>';
  }

  function showBanner(charId, text) {
    const c = CHAR_BY_ID[charId];
    if (!c) return;
    const banner = document.createElement('div');
    banner.className = 'trait-banner';
    banner.innerHTML = '<span class="avatar avatar--sm">' + portraitHTML(charId) + '</span>' +
      '<span class="trait-banner__name">' + esc(c.name) + '</span>' +
      '<span class="trait-banner__text">' + esc(text) + '</span>';
    document.body.appendChild(banner);
    window.setTimeout(function () {
      if (banner.parentNode) banner.parentNode.removeChild(banner);
    }, 2400);
  }

  function avatarHTML(charId, cls) {
    const c = CHAR_BY_ID[charId];
    if (!c) return '';
    return '<span class="avatar ' + (cls || '') + '">' + portraitHTML(charId) + '</span>';
  }

  function tileHTML(code, opts) {
    opts = opts || {};
    const info = tileInfo(code);
    const cls = ['tile'];
    if (opts.cls) cls.push(opts.cls);
    if (opts.marked) cls.push('is-marked');
    if (opts.selected) cls.push('is-selected');
    if (opts.revealed) cls.push('is-revealed');
    if (opts.fresh) cls.push('is-fresh');
    if (opts.cast) cls.push('is-cast');
    const tag = opts.button ? 'button' : 'span';
    const attrs = opts.button
      ? ' type="button" data-action="' + esc(opts.action || '') + '"'
        + (opts.uid !== undefined && opts.uid !== null ? ' data-uid="' + esc(opts.uid) + '"' : '')
        + ' aria-pressed="' + (opts.selected ? 'true' : 'false') + '"'
        + (opts.disabled ? ' disabled' : '')
      : '';
    return '<' + tag + ' class="' + cls.join(' ') + '"' + attrs +
      ' aria-label="' + esc(info.name) + '"' + (opts.title ? ' title="' + esc(opts.title) + '"' : '') +
      '>' + tileFaceHTML(code) + '</' + tag + '>';
  }

  function backTileHTML(cls) {
    return '<span class="tile tile--back ' + (cls || '') + '" aria-hidden="true"></span>';
  }

  function reducedMotion() {
    return document.documentElement.getAttribute('data-motion') === 'reduced';
  }

  /* ==========================================================
     5. 导航与转场
     ========================================================== */

  const SCREEN_TITLE = {
    menu: '主菜单', mode: '选择模式', characters: '动物角色选择', lobby: '联机大厅',
    room: '房间准备', table: '牌桌', codex: '秘籍 / 图鉴', settings: '设置', settlement: '局末结算'
  };

  function veil() {
    if (reducedMotion()) return;
    const v = document.createElement('div');
    v.className = 'transition-veil';
    document.body.appendChild(v);
    window.setTimeout(function () {
      v.classList.add('is-active');
      window.setTimeout(function () { if (v.parentNode) v.parentNode.removeChild(v); }, 460);
    }, 10);
  }

  function navigate(screen, opts) {
    opts = opts || {};
    if (state.screen === screen && !opts.force) return;
    if (opts.push !== false && state.screen && state.screen !== screen) {
      state.navStack.push(state.screen);
      if (state.navStack.length > 12) state.navStack.shift();
    }
    state.screen = screen;
    clearTimers();

    $$('.screen').forEach(function (sec) {
      const on = sec.getAttribute('data-screen') === screen;
      sec.classList.toggle('is-active', on);
      if ('inert' in sec) sec.inert = !on;
      sec.setAttribute('aria-hidden', on ? 'false' : 'true');
    });

    const crumb = $('#crumb');
    if (crumb) crumb.textContent = SCREEN_TITLE[screen] || screen;
    document.body.setAttribute('data-screen', screen);
    document.title = (SCREEN_TITLE[screen] ? SCREEN_TITLE[screen] + ' · ' : '') + '疯狂动物麻将 UI 原型';

    veil();
    closeModal();

    const renderer = RENDERERS[screen];
    if (renderer) renderer();

    const target = $('#screen-' + screen);
    if (target) {
      const focusable = target.querySelector('[data-autofocus]') ||
        target.querySelector('.page-title') || target.querySelector('.title') || target;
      if (focusable) {
        if (!focusable.hasAttribute('tabindex') && focusable === target) focusable.setAttribute('tabindex', '-1');
        focusable.focus({ preventScroll: true });
      }
    }
    updatePointsChip();
    saveSession();
  }

  function goBack() {
    const prev = state.navStack.pop();
    if (!prev) { navigate('menu', { push: false, force: true }); return; }
    navigate(prev, { push: false, force: true });
  }

  /* ==========================================================
     6. 出千点显示
     ========================================================== */

  function updatePointsChip() {
    const chip = $('#points-chip');
    const value = $('#points-value');
    if (!chip || !value) return;
    if (!state.round) { chip.hidden = true; return; }
    chip.hidden = false;
    if (value.textContent !== String(state.round.points)) {
      value.textContent = String(state.round.points);
      chip.classList.remove('is-bump');
      void chip.offsetWidth;
      chip.classList.add('is-bump');
    }
  }

  /* ==========================================================
     7. 费用结算与角色特性
     ========================================================== */

  function currentChar() {
    return CHAR_BY_ID[state.characterId] || CHARACTERS[0];
  }

  /**
   * 依据当前角色与本局施术记录，计算某绝技的实际费用。
   * 返回 { cost, notes[] }；notes 为触发的角色特性说明。
   *
   * commit 为 true 时表示「即将确认施术」，此时会把「猴子每局只减免一次」的
   * 一次性标记落盘；仅做预览（按钮禁用原因、弹窗费用预览）时应传 false。
   */
  function resolveCost(techId, commit) {
    const round = state.round;
    const char = currentChar();
    const base = CHEAT_BY_ID[techId].baseCost;
    let cost = base;
    const notes = [];

    const used = round ? round.usedCheats : [];
    const distinct = used.filter(function (t, i) { return used.indexOf(t) === i; });
    const isFirstCheat = used.length === 0;
    // 「不同绝技」= 本局尚未使用过的绝技种类
    const isNewDistinct = used.length >= 1 && distinct.indexOf(techId) < 0;
    // 猴子的减免只在本局「第二种不同绝技」首次出现时给一次，之后不再给
    const monkeyAlreadyUsed = !!(round && round.monkeyDiscountUsed);

    if (char.id === 'panda') {
      if (isFirstCheat) {
        cost = Math.floor(cost / 2);
        notes.push('熊猫 · 当局第一招半价（' + base + ' → ' + cost + '）');
      } else if (used.length === 1) {
        cost += 3;
        notes.push('熊猫 · 当局第二招 +3 点（' + base + ' → ' + cost + '）');
      }
    }
    if (char.id === 'magpie' && techId === 'tileMark' && round && round.markCount === 0) {
      cost -= 4;
      notes.push('喜鹊 · 每局首次落汗 -4 点（' + base + ' → ' + cost + '）');
    }
    if (char.id === 'monkey' && isNewDistinct && !monkeyAlreadyUsed) {
      cost -= 3;
      notes.push('猴子 · 同局第二种不同绝技 -3 点（' + base + ' → ' + cost + '）');
      if (commit && round) round.monkeyDiscountUsed = true;
    }
    if (char.id === 'crow' && techId === 'beamSwap') {
      cost -= 2;
      notes.push('乌鸦 · 偷梁换柱 -2 点（' + base + ' → ' + cost + '）');
    }
    return { cost: Math.max(0, cost), base: base, notes: notes };
  }

  function maxMarks() {
    return currentChar().id === 'tiger' ? 16 : 12;
  }

  /* ==========================================================
     8. 牌桌数据构建
     ========================================================== */

  let timers = [];
  function clearTimers() {
    timers.forEach(function (t) { window.clearTimeout(t); });
    timers = [];
  }
  function later(fn, ms) {
    const id = window.setTimeout(fn, ms);
    timers.push(id);
    return id;
  }

  function pickOpponentCharacters() {
    const pool = CHARACTERS.filter(function (c) { return c.id !== state.characterId; });
    const shuffled = shuffle(pool.map(function (c) { return c.id; }), Date.now() & 0xffff);
    return shuffled.slice(0, 3);
  }

  function createRound(seed) {
    const raw = shuffle(buildWall(), seed || (Date.now() & 0x7fffffff));
    let uid = 1;
    const wall = raw.map(function (c) { return { c: c, u: uid++ }; });
    const oppChars = pickOpponentCharacters();
    const seats = [];
    for (let i = 0; i < 4; i++) {
      seats.push({
        seat: i,
        name: i === 0 ? '我（' + currentChar().name + '）' : DEMO_NAMES[(i + 1) % DEMO_NAMES.length],
        charId: i === 0 ? state.characterId : oppChars[i - 1],
        isMe: i === 0,
        points: 100,
        hand: []
      });
    }
    const round = {
      index: 1,
      phase: 'playing',
      hand: [],
      drawn: null,
      wall: [],
      discards: [],
      marks: [],
      melds: [],
      points: 100,
      usedCheats: [],
      monkeyDiscountUsed: false,
      markCount: 0,
      selected: null,
      trust: false,
      seats: seats,
      logs: [],
      dora: null,
      turnSeat: 0
    };
    // 牌山实体列表（发牌后剩余 83 张，全部留在 round.wall 上）
    round.wall = wall;

    // 发牌：庄家 14 张，其余 13 张（每张牌都是唯一物理实体）
    for (let s = 0; s < 4; s++) {
      const count = s === 0 ? 14 : 13;
      const tiles = [];
      for (let i = 0; i < count; i++) tiles.push(wall.shift());
      if (s === 0) {
        round.hand = tiles.slice(0, 13);
        round.drawn = tiles[13];
        round.dora = wall[Math.floor(wall.length / 2)].c;
      } else {
        seats[s].hand = tiles;
      }
    }
    round.hand = sortHand(round.hand);
    logLine('发牌完成，庄家「' + currentChar().name + '」起手 13 张 + 摸牌 1 张。', '');
    return round;
  }

  function logLine(text, kind) {
    if (!state.round) return;
    state.round.logs.unshift({ text: text, kind: kind || '' });
    if (state.round.logs.length > 40) state.round.logs.length = 40;
  }

  function seatName(seat) {
    if (!state.round) return '';
    const s = state.round.seats[seat];
    return s ? s.name : '';
  }

  /* ==========================================================
     9. 各页面渲染
     ========================================================== */

  const RENDERERS = {};

  /* ---------- 主菜单 ---------- */
  RENDERERS.menu = function () {
    // 主菜单把「当前角色」做成右侧主视觉（L5）：
    // 章鱼用专用大幅透明立绘，其余角色改用对应肖像图集，保证图文一致，
    // 绝不出现「画面是章鱼、文字却写别的角色」的情况。
    const now = $('#menu-now');
    if (now) {
      const c = currentChar();
      const themeName = (THEMES.filter(function (t) { return t.id === state.settings.theme; })[0] || THEMES[0]).name;
      const lockedCount = Object.keys(state.locked).filter(function (k) { return state.locked[k]; }).length;
      const cheat = charCheatLabel(c.id);
      const art = c.id === 'octopus'
        ? '<img class="menu-hero__photo" src="assets/menu-octopus-hero.png" alt="" decoding="async">'
        : portraitHTML(c.id, { face: false, cls: 'menu-hero__portrait' });
      now.innerHTML =
        '<div class="menu-hero__stage">' +
        '<span class="menu-hero__halo" aria-hidden="true"></span>' +
        '<span class="menu-hero__ring" aria-hidden="true"></span>' +
        '<span class="menu-hero__art">' + art + '</span>' +
        '<span class="menu-hero__seal" aria-hidden="true"><span>雀</span></span>' +
        '</div>' +
        '<figcaption class="menu-hero__plate">' +
        '<span class="gold-fret" aria-hidden="true"></span>' +
        '<span class="menu-hero__kicker">当前角色 · CURRENT</span>' +
        '<span class="menu-hero__name">' + esc(c.name) + '<i>·</i>' + esc(c.trait) + '</span>' +
        '<span class="menu-hero__cheat">关联绝技 · ' + esc(cheat.name) + '</span>' +
        '<span class="menu-hero__trait">' + esc(c.affects) + '</span>' +
        '<span class="menu-hero__meta">皮肤 ' + esc(themeName) + ' · 已锁定 ' + lockedCount + ' 位</span>' +
        '</figcaption>';

      // 章鱼大图若加载失败，回退到角色图集里的章鱼肖像，避免主视觉开天窗。
      const photo = now.querySelector('.menu-hero__photo');
      if (photo) {
        photo.addEventListener('error', function () {
          const holder = photo.parentNode;
          if (!holder) return;
          holder.innerHTML = portraitHTML('octopus', { face: false, cls: 'menu-hero__portrait' });
        });
      }
    }
    const cont = $('#menu-continuation');
    if (cont) {
      if (state.round) {
        cont.hidden = false;
        cont.innerHTML = '上次进度：东风战 第 ' + state.round.index + ' 局 · 出千点 ' + state.round.points +
          ' · 手牌 ' + state.round.hand.length + ' 张。' +
          ' <button type="button" class="btn btn--sm" data-action="resume-table">继续牌桌</button>' +
          ' <button type="button" class="btn btn--sm btn--ghost" data-action="restart-demo">重新开始</button>';
      } else {
        cont.hidden = true;
        cont.innerHTML = '';
      }
    }
    replayMenuEntrance();
  };

  /**
   * 重放主菜单入场动效。
   * 菜单每次进入（含从其他页面返回）时，六个入口都要从右侧依次移入；
   * 做法是移除并重新加上 .is-entering，中间强制一次重排以重启 CSS 动画。
   * 「关闭动画」或 prefers-reduced-motion 下该动画被 CSS 取消，页面直接显示终态。
   */
  function replayMenuEntrance() {
    const sec = $('#screen-menu');
    if (!sec) return;
    sec.classList.remove('is-entering');
    void sec.offsetWidth;
    sec.classList.add('is-entering');
  }

  /* ---------- 模式选择 ---------- */
  RENDERERS.mode = function () { /* 静态内容即可 */ };

  /* ---------- 动物角色选择 ---------- */
  RENDERERS.characters = function () {
    const grid = $('#char-grid');
    if (grid) {
      grid.innerHTML = CHARACTERS.map(function (c) {
        const locked = !!state.locked[c.id];
        const selected = state.characterId === c.id;
        const cheat = charCheatLabel(c.id);
        return '<li class="char-item">' +
          '<div class="char-card' + (selected ? ' is-selected' : '') + (locked ? ' is-locked' : '') + '">' +
          '<button type="button" class="char-card__select" data-action="select-character" data-id="' + c.id + '"' +
          ' aria-pressed="' + (selected ? 'true' : 'false') + '"' +
          ' aria-label="' + esc(c.name + '，' + c.trait + '，关联绝技 ' + cheat.name + (locked ? '，已锁定' : '')) + '">' +
          '<span class="char-card__figure">' + portraitHTML(c.id, { face: false }) + '</span>' +
          '<span class="char-card__top">' +
          '<span class="char-card__name">' + esc(c.name) + '</span>' +
          '<span class="char-card__en">' + esc(c.en) + '</span>' +
          '</span>' +
          '<span class="char-card__trait">' + esc(c.trait) + '</span>' +
          '<span class="char-card__skill"><span class="char-card__glyph" aria-hidden="true">' + esc(cheat.glyph) + '</span>' +
          esc(cheat.name) + '</span>' +
          '</button>' +
          '<button type="button" class="char-card__lock" data-action="toggle-lock" data-id="' + c.id + '"' +
          ' aria-pressed="' + (locked ? 'true' : 'false') + '"' +
          ' aria-label="' + esc((locked ? '解锁' : '锁定') + '角色 ' + c.name) + '" title="' +
          (locked ? '已锁定，点击解锁' : '锁定该角色（演示）') + '">' + (locked ? '🔒' : '🔓') + '</button>' +
          (selected ? '<span class="char-card__badge">已选用</span>' : '') +
          '</div></li>';
      }).join('');
    }

    const detail = $('#char-detail');
    const confirmBtn = $('#char-confirm');
    const c = currentChar();
    const locked = !!state.locked[c.id];
    if (detail) {
      detail.innerHTML =
        '<div class="gold-fret" aria-hidden="true"></div>' +
        '<div class="char-detail__head"><span class="char-detail__figure">' + portraitHTML(c.id, { face: false }) + '</span>' +
        '<div><div class="char-detail__name">' + esc(c.name) + '</div>' +
        '<div class="char-detail__en">' + esc(c.en) + '</div></div></div>' +
        '<div class="char-detail__trait">' + esc(c.trait) + '</div>' +
        '<p class="char-detail__desc">' + esc(c.desc) + '</p>' +
        '<ul class="char-detail__list">' +
        '<li class="char-detail__row"><span class="dot dot--warn" aria-hidden="true"></span>' +
        '<span><b>影响绝技：</b>' + esc(c.affects) + '</span></li>' +
        c.notes.map(function (n) {
          return '<li class="char-detail__row"><span class="dot dot--warn" aria-hidden="true"></span><span>' + esc(n) + '</span></li>';
        }).join('') +
        '<li class="char-detail__row"><span class="dot dot--warn" aria-hidden="true"></span>' +
        '<span><b>边界：</b>角色只加强三大绝技或改变费用，没有独立的新技能。</span></li>' +
        '</ul>' +
        '<div class="char-detail__foot">' +
        '<button type="button" class="btn ' + (locked ? '' : 'btn--primary') + '" data-action="toggle-lock" data-id="' + c.id + '">' +
        (locked ? '解锁该角色' : '锁定该角色') + '</button>' +
        '<span class="inline-reason">' + (locked ? '该角色已被锁定，无法选用。可先解锁再确认。' : '') + '</span>' +
        '</div>';
    }
    if (confirmBtn) {
      confirmBtn.disabled = locked;
      confirmBtn.textContent = locked ? '确认使用（已锁定）' : '确认使用「' + c.name + '」';
    }
  };

  /* ---------- 联机大厅 ---------- */
  RENDERERS.lobby = function () {
    const list = $('#room-list');
    if (!list) return;
    list.innerHTML = DEMO_ROOMS.map(function (r, i) {
      const full = r.players >= r.cap;
      return '<li class="room-row' + (i === 0 ? ' is-selected' : '') + '">' +
        '<div><div class="room-row__id">' + esc(r.id) + '</div>' +
        '<div class="room-row__meta"><span>' + esc(r.mode) + '</span>' +
        '<span>' + r.players + ' / ' + r.cap + ' 人</span>' +
        '<span>房主 ' + esc(r.host) + '</span>' +
        '<span class="status ' + (full ? 'status--warn' : 'status--ok') + '">' + esc(r.state) + '</span>' +
        '<span class="tag tag--warn">演示数据</span></div></div>' +
        '<div class="room-row__acts">' +
        '<button type="button" class="btn btn--sm" data-action="join-room" data-room="' + esc(r.id) + '"' +
        (full ? ' disabled title="房间已满"' : '') + '>' + (full ? '已满' : '加入') + '</button>' +
        '<button type="button" class="btn btn--sm btn--ghost" data-action="spectate-room" data-room="' + esc(r.id) + '">观战</button>' +
        '</div></li>';
    }).join('');
  };

  /* ---------- 房间准备 ---------- */
  RENDERERS.room = function () {
    const room = state.room;
    if (!room) return;

    const note = $('#room-mode-note');
    if (note) {
      note.innerHTML = '<span class="tag tag--warn">演示模拟数据</span>' +
        '房间号 <b>' + esc(room.id) + '</b> · ' + esc(room.modeText) +
        ' · 房间内角色与准备状态均为本地模拟。';
    }

    const seatList = $('#seat-list');
    if (seatList) {
      seatList.innerHTML = room.seats.map(function (s, i) {
        const c = CHAR_BY_ID[s.charId];
        return '<li class="seat-row' + (s.isMe ? ' is-me' : '') + '">' +
          '<span class="seat-row__seat">' + esc(s.seatLabel) + '</span>' +
          avatarHTML(s.charId, 'avatar--sm') +
          '<span><span class="seat-row__name">' + esc(s.name) + '</span><br>' +
          '<span class="seat-row__char">' + esc(c ? c.name + ' · ' + c.trait : '—') + '</span></span>' +
          '<span class="status ' + (s.ready ? 'status--ok' : 'status--off') + '">' +
          (s.ready ? '已准备' : '未准备') + '</span>' +
          '</li>';
      }).join('');
    }

    const readyBtn = $('#btn-ready');
    if (readyBtn) {
      const me = room.seats.filter(function (s) { return s.isMe; })[0];
      readyBtn.textContent = me && me.ready ? '取消准备' : '准备';
      readyBtn.classList.toggle('btn--primary', !(me && me.ready));
    }

    const startBtn = $('#btn-start');
    const reason = $('#room-reason');
    const allReady = room.seats.every(function (s) { return s.ready; });
    if (startBtn) {
      startBtn.disabled = !allReady;
      startBtn.title = allReady ? '' : '仍有席位未准备';
    }
    if (reason) {
      reason.textContent = allReady
        ? '全部席位已准备，可以开赛。'
        : '等待其余 ' + room.seats.filter(function (s) { return !s.ready; }).length + ' 个席位准备。';
    }

    const stackGrid = $('#stack-grid');
    if (stackGrid) {
      if (!room.stackSlots) room.stackSlots = DEFAULT_STACK_SLOTS.slice();
      stackGrid.innerHTML = room.stackSlots.map(function (s, i) {
        const selected = room.stackSlot === i;
        return '<li><button type="button" class="stack-slot" role="radio" aria-checked="' + (selected ? 'true' : 'false') +
          '" data-action="select-stack" data-index="' + i + '">' +
          tileHTML(s.code, { cls: 'tile--sm' }) +
          '<span class="stack-slot__name">' + esc(s.label) + '</span>' +
          '</button></li>';
      }).join('');
    }
    const stackConfirm = $('#btn-stack-confirm');
    const stackReason = $('#stack-reason');
    const stackPaid = room.stackPaid;
    if (stackConfirm) {
      stackConfirm.disabled = stackPaid || room.stackSlot === null;
      stackConfirm.textContent = stackPaid ? '码牌已生效' : '确认码牌（30 点）';
    }
    if (stackReason) {
      stackReason.textContent = stackPaid
        ? '已选择「' + esc(room.stackSlots[room.stackSlot].label) + '」，费用 30 点已扣除（演示）。'
        : (room.stackSlot === null ? '请选择一个码牌目标。' : '将消耗 30 出千点（演示）。');
    }
  };

  /* ---------- 牌桌 ---------- */
  // 牌桌局部重绘入口（回合推进、施术确认后调用）
  function renderTable() {
    if (RENDERERS.table) RENDERERS.table();
  }

  RENDERERS.table = function () {
    const round = state.round;
    if (!round) return;
    renderOpponents();
    renderTableMeta();
    renderTableAnchor();
    renderRiver();
    renderWall();
    renderHand();
    renderTraitCard();
    renderLog();
    renderCheatStack();
    renderActionStack();
    updatePointsChip();
  };

  /**
   * 牌桌中央的方位 / 局次视觉锚点（呼应概念图 03 的桌面构图）：
   * 罗盘圆牌显示 prevailing 风向与局数，四周标注四方位席风，
   * 右侧列出牌山剩余与宝牌。全部为 DOM 文本，不遮挡牌河与点击区。
   */
  function renderTableAnchor() {
    const box = $('#table-anchor');
    if (!box || !state.round) return;
    const round = state.round;
    const winds = ['東', '南', '西', '北'];
    const prevailing = winds[(round.index - 1) % 4];
    const remaining = round.wall.length;
    const dora = round.dora ? tileInfo(round.dora).name : '—';
    box.innerHTML =
      '<div class="anchor__dial">' +
        '<div class="anchor__compass">' +
          '<span class="anchor__rose" aria-hidden="true"></span>' +
          '<span class="anchor__ring" aria-hidden="true"></span>' +
          '<span class="anchor__center"><b>' + round.index + '</b><i>局</i></span>' +
        '</div>' +
        '<div class="anchor__marks" aria-hidden="true">' +
          '<span class="anchor__mark anchor__mark--n">北</span>' +
          '<span class="anchor__mark anchor__mark--e">東</span>' +
          '<span class="anchor__mark anchor__mark--s">南</span>' +
          '<span class="anchor__mark anchor__mark--w">西</span>' +
        '</div>' +
      '</div>' +
      '<dl class="anchor__info">' +
        '<div class="anchor__row"><dt>圈风</dt><dd>' + prevailing + '</dd></div>' +
        '<div class="anchor__row"><dt>牌山</dt><dd>剩余 ' + remaining + ' 张</dd></div>' +
        '<div class="anchor__row"><dt>宝牌</dt><dd>' + esc(dora) + '</dd></div>' +
      '</dl>';
  }

  function opponentBacks(count) {
    let backs = '';
    for (let i = 0; i < count; i++) backs += backTileHTML();
    return backs;
  }

  // 左席：紧凑信息卡（含牌背）
  function seatMiniHTML(s) {
    const round = state.round;
    const last = round.discards.filter(function (d) { return d.seat === s.seat; }).pop();
    return '<div class="seat-mini' + (round.turnSeat === s.seat ? ' is-turn' : '') + '">' +
      avatarHTML(s.charId, 'avatar--md') +
      '<div><div class="seat-mini__name">' + esc(s.name) +
      (round.turnSeat === s.seat ? ' <span class="tag tag--gold">思考中</span>' : '') + '</div>' +
      '<div class="seat-mini__meta"><span>手牌 ' + s.hand.length + '</span>' +
      '<span class="seat-mini__points">出千点 ' + s.points + '</span></div>' +
      '<div class="seat-mini__hand" aria-hidden="true">' + opponentBacks(s.hand.length) + '</div>' +
      (last ? '<div class="seat-mini__meta"><span>打出 ' + esc(tileInfo(last.inst.c).name) + '</span></div>' : '') +
      '</div></div>';
  }

  // 上席 / 右席：肖像铭牌（呼应概念图的三边布局）
  function seatPlaqueHTML(s) {
    const round = state.round;
    const last = round.discards.filter(function (d) { return d.seat === s.seat; }).pop();
    return '<span class="seat-plaque__figure">' + portraitHTML(s.charId) + '</span>' +
      '<span class="seat-plaque__text">' +
      '<span class="seat-plaque__name">' + esc(s.name) +
      (round.turnSeat === s.seat ? ' <span class="tag tag--gold">思考中</span>' : '') + '</span>' +
      '<span class="seat-plaque__meta"><span>手牌 ' + s.hand.length + '</span>' +
      '<span class="seat-plaque__points">出千点 ' + s.points + '</span>' +
      (last ? '<span>打出 ' + esc(tileInfo(last.inst.c).name) + '</span>' : '') + '</span>' +
      '<span class="seat-plaque__hand" aria-hidden="true">' + opponentBacks(Math.min(s.hand.length, 14)) + '</span>' +
      '</span>';
  }

  function renderOpponents() {
    const list = $('#opponent-list');
    const top = $('#seat-top');
    const right = $('#seat-right');
    if (!state.round) return;
    const opp = state.round.seats.filter(function (s) { return !s.isMe; });
    // 三边布局：左席在左栏，上席在牌桌顶端，右席在右侧操作栏顶部
    if (list) list.innerHTML = opp.slice(0, 1).map(seatMiniHTML).join('');
    if (top) top.innerHTML = opp.slice(1, 2).map(seatPlaqueHTML).join('');
    if (right) right.innerHTML = opp.slice(2, 3).map(seatPlaqueHTML).join('');
  }

  function renderTableMeta() {
    const box = $('#table-meta');
    if (!box || !state.round) return;
    const round = state.round;
    const dora = round.dora ? tileInfo(round.dora).name : '—';
    const remaining = round.wall.length;
    const phaseText = round.phase === 'myTurn' ? '你的回合' :
      (round.phase === 'opponents' ? '对手行动中' : '等待中');
    const timerPct = round.phase === 'myTurn' ? 100 : 0;
    box.innerHTML =
      '<span class="meta-pill">东风战 · 第 <strong>' + round.index + '</strong> 局</span>' +
      '<span class="meta-pill">牌山剩余 <strong>' + remaining + '</strong> 张</span>' +
      '<span class="meta-pill meta-pill--dora">指示牌 ' + esc(dora) + '</span>' +
      '<span class="meta-pill">' + esc(phaseText) + '</span>' +
      '<span class="meta-pill">计时 <span class="timer-bar"><span class="timer-bar__fill" style="width:' +
      timerPct + '%"></span></span></span>' +
      (round.trust ? '<span class="tag tag--violet">托管中（自动出牌）</span>' : '') +
      '<span class="tag tag--warn">演示模拟数据</span>';
  }

  function renderRiver() {
    const box = $('#river');
    if (!box || !state.round) return;
    const d = state.round.discards;
    if (!d.length) {
      box.innerHTML = '<p class="river__empty">牌河为空，打出的牌会出现在这里。</p>';
      return;
    }
    box.innerHTML = d.map(function (item) {
      const cls = ['tile', 'tile--sm'];
      if (item.marked) cls.push('is-revealed');
      return '<span class="' + cls.join(' ') + '" title="' +
        esc(seatName(item.seat) + ' 打出 ' + tileInfo(item.inst.c).name) + '" aria-label="' +
        esc(tileInfo(item.inst.c).name) + '">' + tileFaceHTML(item.inst.c) + '</span>';
    }).join('');
  }

  function renderWall() {
    const box = $('#wall-row');
    if (!box || !state.round) return;
    const strip = [];
    const shown = Math.min(14, state.round.wall.length);
    for (let i = 0; i < shown; i++) strip.push(backTileHTML());
    box.innerHTML = '<span class="wall-row__label">牌山</span>' +
      '<span class="wall-strip" aria-hidden="true">' + strip.join('') + '</span>' +
      '<span class="meta-pill">剩余 <strong>' + state.round.wall.length + '</strong> 张</span>';
    // 左右两侧的牌山立柱（概念图 03 的桌面构图，纯装饰）
    const stack = function (n) {
      let s = '';
      for (let i = 0; i < n; i++) s += backTileHTML('tile--stack');
      return s;
    };
    const left = $('#wall-left');
    const right = $('#wall-right');
    if (left) left.innerHTML = stack(6);
    if (right) right.innerHTML = stack(6);
  }

  function renderHand() {
    const list = $('#my-hand');
    const head = $('#hand-head');
    const foot = $('#hand-foot');
    const melds = $('#my-melds');
    const round = state.round;
    if (!list || !round) return;

    if (head) {
      const sel = round.selected !== null ? findTileByUid(round, round.selected) : null;
      const total = round.hand.length + (round.drawn ? 1 : 0);
      head.innerHTML = '<span class="hand-head__title">我的手牌</span>' +
        '<span>共 ' + total + ' 张（手牌 ' + round.hand.length + ' 张' +
        (round.drawn ? ' + 摸牌 1 张' : '') + '）</span>' +
        (sel ? '<span class="tag tag--gold">已选 ' + esc(tileInfo(sel.c).name) + '</span>' : '');
    }

    list.innerHTML = round.hand.map(function (inst) {
      const marked = round.marks.indexOf(inst.u) >= 0;
      const pulsing = round.markPulse && round.markPulse.indexOf(inst.u) >= 0;
      return '<li>' + tileHTML(inst.c, {
        button: true, action: 'select-tile', uid: inst.u, marked: marked, cast: pulsing,
        selected: round.selected === inst.u,
        title: marked ? tileInfo(inst.c).name + '（已落汗标记）' : tileInfo(inst.c).name
      }) + '</li>';
    }).join('') +
      (round.drawn
        ? '<li class="hand__sep" aria-hidden="true"></li>' +
          '<li class="hand__drawn-wrap">' + tileHTML(round.drawn.c, {
            button: true, action: 'select-tile', uid: round.drawn.u,
            selected: round.selected === round.drawn.u,
            title: tileInfo(round.drawn.c).name + '（刚摸到的牌）'
          }) + '<span class="hand__drawn-label">摸牌</span></li>'
        : '');

    if (melds) {
      melds.innerHTML = round.melds.length
        ? round.melds.map(function (m) {
          return '<div class="meld-group"><span class="meld-group__label">' + esc(m.label) + '</span>' +
            m.codes.map(function (c) { return tileHTML(c, { cls: 'tile--sm' }); }).join('') + '</div>';
        }).join('')
        : '';
    }

    if (foot) {
      const marks = round.marks.map(function (u) {
        const t = findTileByUid(round, u);
        return t ? tileInfo(t.c).name : '?';
      }).join('、');
      foot.innerHTML = round.marks.length
        ? '<span class="hand-foot__hint">落汗追踪（' + round.marks.length + ' / ' + maxMarks() + '）：<strong>' +
          esc(marks) + '</strong></span>'
        : '<span class="hand-foot__hint">点击手牌选中，再按 <strong>出牌</strong>；或点击 <strong>落汗</strong> 进入标记流程。' +
          '出牌后手牌恒为 13 张，下一巡摸牌后为 14 张。</span>';
    }
  }

  // 按物理实体编号查找一张牌（手牌 / 摸牌 / 牌山 / 牌河）
  function findTileByUid(round, uid) {
    let found = null;
    round.hand.forEach(function (t) { if (t.u === uid) found = t; });
    if (round.drawn && round.drawn.u === uid) found = round.drawn;
    round.wall.forEach(function (t) { if (t.u === uid) found = t; });
    round.discards.forEach(function (d) { if (d.inst.u === uid) found = d.inst; });
    return found;
  }

  /**
   * 预览牌山末端的 n 张实体。
   * 只返回引用，不改变 round.wall 的内容，也不转移任何实体的所有权；
   * 真正的归属转移一律发生在「确认施术」时。
   */
  function previewWall(n) {
    const round = state.round;
    if (!round) return [];
    const count = Math.min(n, round.wall.length);
    const out = [];
    for (let i = 0; i < count; i++) out.push(round.wall[round.wall.length - count + i]);
    return out;
  }

  function renderTraitCard() {
    const body = $('#trait-body');
    if (!body || !state.round) return;
    const c = currentChar();
    const triggered = state.round.traitLog && state.round.traitLog.length > 0;
    body.innerHTML =
      '<div class="trait-card__name">' + esc(c.name) + ' · ' + esc(c.trait) + '</div>' +
      '<div class="trait-card__desc">' + esc(c.affects) + '</div>' +
      '<div class="trait-card__desc">本局触发：' +
      (triggered ? esc(state.round.traitLog.join('；')) : '尚未触发') + '</div>';
  }

  function renderLog() {
    const box = $('#table-log');
    if (!box || !state.round) return;
    box.innerHTML = state.round.logs.map(function (l) {
      return '<li class="' + (l.kind ? 'is-' + l.kind : '') + '">' + esc(l.text) + '</li>';
    }).join('');
  }

  function cheatAvailability(techId) {
    const round = state.round;
    const info = CHEAT_BY_ID[techId];
    const priced = resolveCost(techId);
    if (!round) return { ok: false, why: '未进入对局。', priced: priced };
    if (round.phase !== 'myTurn') return { ok: false, why: '需在自己的回合施术。', priced: priced };
    if (round.points < priced.cost) {
      return { ok: false, why: '出千点不足（需要 ' + priced.cost + ' 点，现有 ' + round.points + ' 点）。', priced: priced };
    }
    if (techId === 'tileMark') {
      if (round.hand.length < 4) return { ok: false, why: '手牌不足 4 张，无法标记。', priced: priced };
      if (round.marks.length >= maxMarks()) {
        return { ok: false, why: '标记已达上限（' + maxMarks() + ' 张）。', priced: priced };
      }
    }
    if (techId === 'sleeveBloom' && !round.drawn) {
      return { ok: false, why: '等待摸牌后才能施术。', priced: priced };
    }
    if (techId === 'beamSwap' && !round.drawn) {
      return { ok: false, why: '需在摸牌后、出牌前施术。', priced: priced };
    }
    if (techId === 'sleeveBloom' && round.wall.length < 2) {
      return { ok: false, why: '牌山不足 2 张，无法生成候选。', priced: priced };
    }
    if (techId === 'beamSwap' && round.wall.length < 1) {
      return { ok: false, why: '牌山已空，无法交换。', priced: priced };
    }
    return { ok: true, why: '', priced: priced };
  }

  function renderCheatStack() {
    const box = $('#cheat-stack');
    if (!box) return;
    box.innerHTML = CHEATS.map(function (c) {
      const av = cheatAvailability(c.id);
      const notes = av.priced.notes.length
        ? '<span class="cheat-btn__cost">原价 ' + av.priced.base + ' · 本次 ' + av.priced.cost + ' 点</span>' +
          '<span class="cheat-btn__id">' + esc(av.priced.notes.join('；')) + '</span>'
        : '<span class="cheat-btn__cost">消耗 ' + av.priced.cost + ' 出千点</span>';
      return '<button type="button" class="cheat-btn" data-action="open-cheat" data-cheat="' + c.id + '"' +
        (av.ok ? '' : ' disabled title="' + esc(av.why) + '"') + '>' +
        '<span class="cheat-btn__glyph" aria-hidden="true">' + esc(c.glyph) + '</span>' +
        '<span><span class="cheat-btn__name">' + esc(c.name) +
        ' <span class="cheat-btn__id">' + esc(c.en) + '</span></span>' + notes +
        (av.ok ? '' : '<span class="cheat-btn__why">不可用：' + esc(av.why) + '</span>') +
        '</span></button>';
    }).join('');
  }

  function renderActionStack() {
    const box = $('#action-stack');
    if (!box || !state.round) return;
    const round = state.round;
    const canDiscard = round.phase === 'myTurn' && round.selected !== null;
    const selTile = round.selected !== null ? findTileByUid(round, round.selected) : null;
    box.innerHTML =
      '<button type="button" class="btn btn--primary" data-action="discard"' + (canDiscard ? '' : ' disabled') + '>' +
      (selTile ? '打出 ' + esc(tileInfo(selTile.c).name) : '出牌（先选牌）') + '</button>' +
      '<div class="action-row">' +
      '<button type="button" class="btn" data-action="sort-hand">理牌</button>' +
      '<button type="button" class="btn" data-action="toggle-trust" aria-pressed="' + (round.trust ? 'true' : 'false') + '">' +
      (round.trust ? '取消托管' : '托管') + '</button>' +
      '</div>' +
      '<div class="action-row">' +
      '<button type="button" class="btn" data-action="settle-now">结算本局</button>' +
      '<button type="button" class="btn" data-action="restart-round">重开本局</button>' +
      '</div>' +
      '<button type="button" class="btn btn--ghost btn--sm" data-action="add-points">补充演示点数（+50）</button>' +
      '<span class="inline-reason">' +
      (round.phase === 'myTurn' ? '轮到你：选牌 → 出牌 / 施术。' : '等待对手行动……') + '</span>';
  }

  /* ---------- 秘籍 / 图鉴 ---------- */
  RENDERERS.codex = function () {
    const cheats = $('#panel-cheats');
    if (cheats) {
      cheats.innerHTML =
        '<div class="codex-grid">' + CHEATS.map(function (c) {
          return '<article class="codex-card">' +
            '<div class="codex-card__head"><span class="cheat-btn__glyph" aria-hidden="true">' + esc(c.glyph) + '</span>' +
            '<div><div class="codex-card__name">' + esc(c.name) + '</div>' +
            '<div class="codex-card__id">' + esc(c.en) + '</div></div>' +
            '<div class="codex-card__cost">基础费用 ' + c.baseCost + ' 出千点</div></div>' +
            '<p class="codex-card__desc">' + esc(c.desc) + '</p>' +
            '<ul class="codex-card__notes">' + c.notes.map(function (n) {
              return '<li>' + esc(n) + '</li>';
            }).join('') + '</ul>' +
            '<p class="codex-card__warn">注意：' + esc(c.warn) + '</p>' +
            '</article>';
        }).join('') +
        '<article class="codex-card">' +
        '<div class="codex-card__head"><span class="cheat-btn__glyph" aria-hidden="true">码</span>' +
        '<div><div class="codex-card__name">统一码牌</div><div class="codex-card__id">SHUFFLE_STACKING</div></div>' +
        '<div class="codex-card__cost">30 出千点</div></div>' +
        '<p class="codex-card__desc">准备阶段六选一：选定一张牌作为目标，同时作用于起手与本局全部正常预计摸牌槽位。</p>' +
        '<ul class="codex-card__notes"><li>看破（10 点）可叠加，合计最高 40 点</li>' +
        '<li>看破抵消对手整局码牌并清除其落汗，双方费用照付</li></ul>' +
        '</article></div>';
    }

    const chars = $('#panel-characters');
    if (chars) {
      chars.innerHTML = '<div class="codex-char-grid">' + CHARACTERS.map(function (c) {
        return '<article class="codex-char">' + avatarHTML(c.id, 'avatar--sm') +
          '<div><div class="codex-char__name">' + esc(c.name) + ' · ' + esc(c.trait) + '</div>' +
          '<div class="codex-char__trait">' + esc(c.affects) + '</div>' +
          '<div class="codex-char__desc">' + esc(c.desc) + '</div></div></article>';
      }).join('') + '</div>';
    }

    const boundary = $('#panel-boundary');
    if (boundary) {
      boundary.innerHTML = '<div class="panel"><h3 class="panel__title">原型与真实游戏的边界</h3>' +
        '<ul class="boundary-list">' +
        '<li>本页面是<b>纯前端 UI 原型</b>：HTML + CSS + 原生 JS，无构建步骤、无 CDN、无网络请求；' +
        '本地 PNG 只作场景层与角色肖像，<b>所有文字、牌面、状态与按钮均由 HTML/CSS/JS 绘制</b>。</li>' +
        '<li>房间、牌局、角色、结算数据全部由本地脚本生成，属于<b>演示模拟数据</b>，不来自任何服务器。</li>' +
        '<li>原型<b>不会发起任何网络请求</b>，也不包含用户账户、全自动撮合匹配、TLS/加密信道等未实现功能。</li>' +
        '<li>动物角色为<b>原型提出的可选角色</b>，只加强三大绝技或改变费用，<b>尚未在真实游戏中落地</b>。</li>' +
        '<li>真实游戏侧仍以原有实现为准：三大绝技落汗 / 袖里藏花 / 偷梁换柱、单副牌 136 张物理守恒、独立出千点账户。</li>' +
        '<li>费用、标记上限等数值沿用原型假设，便于展示交互；与生产规则如有出入，以生产实现为准。</li>' +
        '</ul></div>';
    }
  };

  /* ---------- 设置 ---------- */
  RENDERERS.settings = function () {
    const themeBox = $('#theme-choice');
    if (themeBox) {
      themeBox.innerHTML = THEMES.map(function (t) {
        return '<button type="button" class="choice" role="radio" data-action="set-theme" data-value="' + t.id + '"' +
          ' aria-checked="' + (state.settings.theme === t.id ? 'true' : 'false') + '">' + esc(t.name) + '</button>';
      }).join('');
    }
    const motionBox = $('#motion-choice');
    if (motionBox) {
      motionBox.innerHTML = MOTION_MODES.map(function (m) {
        return '<button type="button" class="choice" role="radio" data-action="set-motion" data-value="' + m.id + '" aria-checked="' +
          (state.settings.motion === m.id ? 'true' : 'false') + '">' + esc(m.name) + '</button>';
      }).join('');
    }
    const sizeBox = $('#textsize-choice');
    if (sizeBox) {
      sizeBox.innerHTML = [['sm', '小'], ['md', '标准'], ['lg', '大']].map(function (p) {
        return '<button type="button" class="choice" role="radio" data-action="set-textsize" data-value="' + p[0] +
          '" aria-checked="' + (state.settings.textsize === p[0] ? 'true' : 'false') + '">' + esc(p[1]) + '</button>';
      }).join('');
    }
    const octo = $('#setting-octopus-demo');
    if (octo) octo.checked = !!state.settings.octopusDemo;

    const stats = $('#settings-stats');
    if (stats) {
      const c = currentChar();
      const rows = [
        ['当前角色', c.name + ' · ' + c.trait],
        ['牌面皮肤', (THEMES.filter(function (t) { return t.id === state.settings.theme; })[0] || THEMES[0]).name],
        ['动效强度', (MOTION_MODES.filter(function (m) { return m.id === state.settings.motion; })[0] || MOTION_MODES[0]).name +
          '（当前：' + (effectiveMotion() === 'reduced' ? '已关闭动画' : '完整动画') + '）'],
        ['界面字号', { sm: '小', md: '标准', lg: '大' }[state.settings.textsize]],
        ['已锁定角色', Object.keys(state.locked).filter(function (k) { return state.locked[k]; }).length + ' 位'],
        ['本地存档', '浏览器 localStorage（' + STORAGE_KEY + '）']
      ];
      stats.innerHTML = rows.map(function (r) {
        return '<dt>' + esc(r[0]) + '</dt><dd>' + esc(r[1]) + '</dd>';
      }).join('');
    }
  };

  /* ---------- 结算 ---------- */
  RENDERERS.settlement = function () {
    const box = $('#settlement-body');
    if (!box) return;
    const round = state.round;
    const char = currentChar();

    const results = buildDemoResults(round);
    const winner = results[0];

    const rankHTML = results.map(function (r, i) {
      const c = CHAR_BY_ID[r.charId];
      return '<article class="rank-card' + (i === 0 ? ' is-first' : '') + '">' +
        '<div class="rank-card__head"><span class="rank-card__rank">' + (i + 1) + '</span>' +
        avatarHTML(r.charId, 'avatar--sm') +
        '<div><div class="rank-card__name">' + esc(r.name) + '</div>' +
        '<div class="seat-row__char">' + esc(c ? c.name + ' · ' + c.trait : '') + '</div></div></div>' +
        '<div class="rank-card__score">' + (r.score >= 0 ? '+' : '') + r.score + '</div>' +
        '<div class="rank-card__meta"><span>' + esc(r.yaku) + '</span>' +
        '<span>出千点 ' + r.points + '（' + (r.pointsDelta >= 0 ? '+' : '') + r.pointsDelta + '）</span></div>' +
        '</article>';
    }).join('');

    const ledgerRows = (round && round.ledger ? round.ledger : []).map(function (e) {
      return '<div class="ledger__row"><span>' + esc(e.tech) + '</span>' +
        '<span>' + esc(e.trait || '—') + '</span>' +
        '<b>-' + e.cost + ' 点</b></div>';
    }).join('') || '<div class="ledger__row"><span>本局未使用出千绝技</span><span>—</span><b>0 点</b></div>';

    const totalCost = (round && round.ledger ? round.ledger : [])
      .reduce(function (a, b) { return a + b.cost; }, 0);

    const traitText = round && round.traitLog && round.traitLog.length
      ? round.traitLog.join('；')
      : '本局未触发角色特性';

    box.innerHTML =
      '<div class="settle-hero"><div>' +
      '<div class="settle-hero__title">' + esc(winner.name) + ' 和了（演示）</div>' +
      '<div class="settle-hero__sub">东风战 第 ' + (round ? round.index : 1) + ' 局 · ' +
      esc(winner.yaku) + ' · ' + esc(winner.score) + ' 点</div></div>' +
      '<div><span class="tag tag--gold">演示模拟数据</span>' +
      '<div class="settle-hero__sub">角色特性结算：' + esc(traitText) + '</div></div>' +
      '<div class="settle-confetti" aria-hidden="true">' +
      '<i style="--i:0"></i><i style="--i:1"></i><i style="--i:2"></i><i style="--i:3"></i>' +
      '<i style="--i:4"></i><i style="--i:5"></i><i style="--i:6"></i><i style="--i:7"></i>' +
      '</div></div>' +
      '<div class="rank-list">' + rankHTML + '</div>' +
      '<div class="ledger"><h3 class="panel__title">出千账本 <span class="panel__sub">本局 ' + esc(char.name) +
      ' 的绝技使用与费用</span></h3>' +
      '<div class="ledger__rows">' + ledgerRows + '</div>' +
      '<div class="ledger__total"><span>本局出千点合计消耗</span><b>' + totalCost + ' 点</b></div>' +
      '<div class="ledger__total"><span>当前出千点余额</span><b>' + (round ? round.points : 0) + ' 点</b></div>' +
      '</div>';
  };

  function buildDemoResults(round) {
    const seats = round ? round.seats : [];
    const yakuPool = ['立直 · 断幺九 · 门前清', '平和 · 一发', '断幺九 · 无役', '役牌 混一色'];
    const results = seats.map(function (s, i) {
      const isMe = s.isMe;
      const score = isMe ? (round && round.phase === 'win' ? 12000 : -1500) : [12000, -6000, -3500, -2500][i] || 0;
      return {
        name: s.name,
        charId: s.charId,
        score: score,
        yaku: isMe ? (round && round.phase === 'win' ? '立直 · 断幺九 · 门前清' : '未和了（演示数据）') : yakuPool[i % yakuPool.length],
        points: s.points,
        pointsDelta: s.points - 100
      };
    });
    results.sort(function (a, b) { return b.score - a.score; });
    return results;
  }

  /* ==========================================================
     10. 弹窗（出千流程）
     ========================================================== */

  let modalSeq = 0;
  let modalEl = null;
  let lastFocus = null;

  function closeModal() {
    if (!modalEl) return;
    const el = modalEl;
    modalEl = null;
    if (el.parentNode) el.parentNode.removeChild(el);
    document.removeEventListener('keydown', modalKeyHandler, true);
    // 候选仅作预览，退出时无需归还；但仍清空待确认状态，避免残留
    if (state.round) {
      state.round.pendingBloom = null;
      state.round.pendingSwap = null;
    }
    if (lastFocus && document.contains(lastFocus)) lastFocus.focus({ preventScroll: true });
    lastFocus = null;
  }

  function modalKeyHandler(e) {
    if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      closeModal();
      return;
    }
    if (e.key !== 'Tab' || !modalEl) return;
    const focusables = $$('button:not([disabled]), input:not([disabled]), [tabindex]:not([tabindex="-1"])', modalEl)
      .filter(function (n) { return n.offsetParent !== null; });
    if (!focusables.length) return;
    const first = focusables[0];
    const last = focusables[focusables.length - 1];
    if (e.shiftKey && document.activeElement === first) {
      e.preventDefault(); last.focus();
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault(); first.focus();
    }
  }

  function openModal(opts) {
    closeModal();
    lastFocus = document.activeElement;
    modalSeq += 1;
    const titleId = 'modal-title-' + modalSeq;
    const overlay = document.createElement('div');
    overlay.className = 'overlay';
    overlay.innerHTML =
      '<div class="modal" role="dialog" aria-modal="true" aria-labelledby="' + titleId + '">' +
      '<div class="modal__head"><div><h3 class="modal__title" id="' + titleId + '"></h3>' +
      '<p class="modal__sub"></p></div>' +
      '<button type="button" class="btn btn--ghost btn--sm modal__close" data-action="close-modal">关闭 (Esc)</button></div>' +
      '<div class="modal__body"></div><div class="modal__foot"></div></div>';
    overlay.addEventListener('mousedown', function (e) {
      if (e.target === overlay) closeModal();
    });
    document.getElementById('overlay-root').appendChild(overlay);
    modalEl = overlay;
    $('.modal__title', overlay).textContent = opts.title || '';
    $('.modal__sub', overlay).textContent = opts.sub || '';
    if (opts.body) $('.modal__body', overlay).innerHTML = opts.body;
    if (opts.foot) $('.modal__foot', overlay).innerHTML = opts.foot;
    document.addEventListener('keydown', modalKeyHandler, true);
    const auto = $('[data-autofocus]', overlay) || $('.modal__foot .btn--primary', overlay) ||
      $('.modal__body button', overlay) || $('.modal__close', overlay);
    if (auto) auto.focus({ preventScroll: true });
    return overlay;
  }

  /* ==========================================================
     11. 三个绝技的交互流程
     ========================================================== */

  function openTileMarkModal() {
    const round = state.round;
    if (!round) return;
    const av = cheatAvailability('tileMark');
    if (!av.ok) { toast(av.why, 'warn'); return; }

    const selected = [];
    const body = document.createElement('div');
    const foot = document.createElement('div');
    foot.innerHTML =
      '<button type="button" class="btn btn--ghost" data-action="close-modal">取消</button>' +
      '<button type="button" class="btn btn--primary" data-action="confirm-tile-mark" disabled>确认落汗（4 张）</button>';

    function renderBody() {
      const priced = resolveCost('tileMark');
      const picks = round.hand.map(function (inst) {
        const on = selected.indexOf(inst.u) >= 0;
        const already = round.marks.indexOf(inst.u) >= 0;
        return '<button type="button" class="pick" role="radio" aria-checked="' + (on ? 'true' : 'false') + '"' +
          ' data-action="toggle-mark-pick" data-uid="' + inst.u + '"' +
          (already ? ' disabled title="该牌已被标记"' : '') + '>' +
          tileHTML(inst.c, { selected: on, marked: already }) +
          '<span class="pick__label">' + esc(tileInfo(inst.c).name) +
          (already ? '（已标记）' : '') + '</span></button>';
      }).join('');

      body.innerHTML =
        '<div class="cast-legend"><span class="cast-legend__glyph" aria-hidden="true">汗</span>' +
        '<span>落汗 · 从手牌中选 <strong>4 张</strong>实体牌留下微痕标记；标记上限 ' + maxMarks() +
        ' 张（当前 ' + round.marks.length + ' 张）。</span></div>' +
        costPreviewHTML(priced, { extra: '已选 ' + selected.length + ' / 4' }) +
        '<p class="note">从手牌中选择 <strong>4 张</strong>实体牌留下微痕标记。标记牌进入对手手牌时可识别牌面，打入牌河时精准定位。' +
        '标记上限 <strong>' + maxMarks() + '</strong> 张（当前 ' + round.marks.length + ' 张）。</p>' +
        '<div class="pick-grid pick-grid--mark">' + picks + '</div>' +
        '<p class="note note--warn">演示提示：真实游戏中标记在当局立即生效并持续后续 3 局；思考超时仅禁止新施术，费用不退。</p>';
      const confirmBtn = $('[data-action="confirm-tile-mark"]', foot);
      if (confirmBtn) confirmBtn.disabled = selected.length !== 4;
    }

    const overlay = openModal({
      title: '落汗 · TILE_MARK',
      sub: '选择 4 张手牌留下微痕标记（演示模拟）',
      body: '<div id="tm-body"></div>',
      foot: ''
    });
    // 将节点挂到弹窗内部（foot 使用 display:contents 以继承弹窗底部按钮的布局）
    const modalBody = $('#tm-body', overlay).parentNode;
    modalBody.appendChild(body);
    foot.style.display = 'contents';
    $('.modal__foot', overlay).appendChild(foot);

    body.addEventListener('click', function (e) {
      const btn = e.target.closest('[data-action="toggle-mark-pick"]');
      if (!btn || btn.disabled) return;
      const uid = parseInt(btn.getAttribute('data-uid'), 10);
      const idx = selected.indexOf(uid);
      if (idx >= 0) selected.splice(idx, 1);
      else if (selected.length < 4) selected.push(uid);
      else { toast('最多只能选择 4 张牌。', 'warn'); return; }
      renderBody();
    });

    renderBody();
  }

  function confirmTileMark() {
    const round = state.round;
    if (!round) return;
    const selected = $$('[data-action="toggle-mark-pick"][aria-checked="true"]', modalEl || document)
      .map(function (b) { return parseInt(b.getAttribute('data-uid'), 10); });
    if (selected.length !== 4) { toast('请选择恰好 4 张牌。', 'warn'); return; }

    // ---- 有效性检查（只读，不提交任何折扣标记）----
    const preview = resolveCost('tileMark');
    if (round.points < preview.cost) { toast('出千点不足。', 'warn'); return; }

    // ---- 全部检查通过后才提交：此时才消费猴子的一次性折扣 ----
    const priced = resolveCost('tileMark', true);

    round.points -= priced.cost;
    round.usedCheats.push('tileMark');
    round.markCount += 1;
    selected.forEach(function (uid) { if (round.marks.indexOf(uid) < 0) round.marks.push(uid); });
    // 落汗落点动画：水滴落在刚标记的实体牌上（降低动态效果时自动退化为静态标记）
    round.markPulse = selected.slice();
    round.ledger = round.ledger || [];
    round.ledger.push({ tech: '落汗', cost: priced.cost, trait: priced.notes.join('；') || '—' });
    round.traitLog = round.traitLog || [];

    const char = currentChar();
    if (priced.notes.length) {
      priced.notes.forEach(function (n) { round.traitLog.push(n); });
      showBanner(char.id, priced.notes.join('；'));
      toast('角色特性触发：' + priced.notes.join('；'), 'trait');
      logLine('角色特性触发 · ' + priced.notes.join('；'), 'trait');
      const card = $('#trait-card');
      if (card) { card.classList.remove('is-triggered'); void card.offsetWidth; card.classList.add('is-triggered'); }
    }

    logLine('落汗完成：标记 ' + selected.map(function (uid) {
      const t = findTileByUid(round, uid);
      return t ? tileInfo(t.c).name : '?';
    }).join('、') + '，消耗 ' + priced.cost + ' 点。', 'cheat');
    closeModal();
    renderTable();
    later(function () { if (state.round) { state.round.markPulse = null; } }, 720);
    saveSession();
    announce('落汗成功，消耗 ' + priced.cost + ' 出千点，剩余 ' + round.points + ' 点。');
    toast('落汗成功：已标记 4 张牌（-' + priced.cost + ' 点）。', 'cheat');
    const btn = $('[data-action="open-cheat"][data-cheat="tileMark"]');
    if (btn) { btn.classList.remove('is-casting'); void btn.offsetWidth; btn.classList.add('is-casting'); }
  }

  function openSleeveBloomModal() {
    const round = state.round;
    if (!round) return;
    const av = cheatAvailability('sleeveBloom');
    if (!av.ok) { toast(av.why, 'warn'); return; }

    const char = currentChar();
    const isOctopus = char.id === 'octopus';
    let triggered = false;
    if (isOctopus) {
      triggered = state.settings.octopusDemo ? true : (Math.random() < 0.2);
    }
    const candidateCount = triggered ? 3 : 2;

    // 待确认状态统一由 pending 这一个对象承载：UI 与 state.round.pendingBloom
    // 始终指向同一份数据。强制触发三候选时只改这个对象的属性，
    // 不会再出现「界面显示 3 张、待确认数组仍是 2 张」的不同步。
    const pending = {
      candidates: previewWall(candidateCount),
      original: round.drawn,
      triggered: triggered,
      picked: null,
      forceDemo: state.settings.octopusDemo,
      forced: false   // 候选数是否由「强制触发」从 2 张提升到 3 张
    };

    const body = document.createElement('div');
    const foot = document.createElement('div');
    foot.innerHTML =
      '<button type="button" class="btn btn--ghost" data-action="close-modal">取消</button>' +
      (isOctopus ? '<button type="button" class="btn" data-action="force-octopus">演示：强制触发三候选</button>' : '') +
      '<button type="button" class="btn btn--primary" data-action="confirm-sleeve-bloom" disabled>确认保留</button>';

    function candidateList() {
      const list = [];
      const octoTrigger = pending.triggered && pending.candidates.length >= 3;
      list.push('<button type="button" class="pick pick--tile is-original" role="radio" aria-checked="' +
        (pending.picked === 'original' ? 'true' : 'false') + '" data-action="pick-bloom" data-pick="original">' +
        (pending.original ? tileHTML(pending.original.c, { selected: pending.picked === 'original' }) : '') +
        '<span class="pick__label">原摸牌' + (char.id === 'penguin' ? '（企鹅：返还 3 点）' : '') + '</span></button>');
      pending.candidates.forEach(function (inst, i) {
        // 章鱼特性触发时，多出来的第 3 张候选会被明确标出（20% 触发 · 三选一）
        const extra = octoTrigger && i === pending.candidates.length - 1;
        list.push('<button type="button" class="pick pick--tile' + (extra ? ' pick--octopus' : '') + '" role="radio" aria-checked="' +
          (pending.picked === 'c' + i ? 'true' : 'false') + '" data-action="pick-bloom" data-pick="c' + i + '">' +
          tileHTML(inst.c, { selected: pending.picked === 'c' + i }) +
          '<span class="pick__label">' + (extra ? '候选 ' + (i + 1) + ' · 20% 触发' : '候选 ' + (i + 1)) + '</span></button>');
      });
      return list.join('');
    }

    function renderBody() {
      const priced = resolveCost('sleeveBloom');
      let refund = 0;
      if (char.id === 'penguin' && pending.picked === 'original') refund = 3;
      body.innerHTML =
        '<div class="cast-legend"><span class="cast-legend__glyph" aria-hidden="true">花</span>' +
        '<span>袖里藏花 · 摸牌后保留一张，其余候选返回牌山' +
        (pending.triggered ? '（本次触发三候选，仍只保留一张）' : '（默认二选一）') + '。</span></div>' +
        costPreviewHTML(priced, { refund: refund }) +
        '<div class="pick-grid pick-grid--bloom">' + candidateList() + '</div>' +
        (isOctopus
          ? '<p class="note note--jade">章鱼特性：使用袖里藏花时 <strong>20% 概率</strong>看到三张候选，但<strong>只保留一张</strong>。' +
            '当前' + (pending.triggered ? '已' : '未') + '触发' + (pending.triggered ? '（本次演示为三选一）' : '（本次为二选一）') + '。' +
            '无论出现几张候选，手牌数量都不变。</p>'
          : '<p class="note">默认二选一保留。候选只是对牌山的<b>预览</b>：确认之前不会移动任何实体，' +
            '因此取消或关闭不会丢牌，<strong>物理守恒</strong>。</p>') +
        '<p class="note note--warn">演示提示：真实游戏中袖里藏花可在他家回合预定，待本人下次普通摸牌入手；超时或预定后不退款。</p>';
      const confirmBtn = $('[data-action="confirm-sleeve-bloom"]', foot);
      if (confirmBtn) confirmBtn.disabled = pending.picked === null;
      const forceBtn = $('[data-action="force-octopus"]', foot);
      if (forceBtn) {
        const isThree = pending.candidates.length >= 3;
        forceBtn.textContent = isThree ? '演示：回到二选一' : '演示：强制触发三候选';
        forceBtn.setAttribute('aria-pressed', isThree ? 'true' : 'false');
      }
    }

    const overlay = openModal({
      title: '袖里藏花 · SLEEVE_BLOOM',
      sub: '摸牌后从候选中保留一张（演示模拟）',
      body: '<div id="sb-body"></div>',
      foot: ''
    });
    const modalBody = $('#sb-body', overlay).parentNode;
    modalBody.appendChild(body);
    foot.style.display = 'contents';
    $('.modal__foot', overlay).appendChild(foot);
    // openModal 内部会先 closeModal() 清空待确认状态，因此这里在弹窗就绪后再挂上
    state.round.pendingBloom = pending;

    body.addEventListener('click', function (e) {
      const btn = e.target.closest('[data-action="pick-bloom"]');
      if (!btn) return;
      pending.picked = btn.getAttribute('data-pick');
      renderBody();
    });

    foot.addEventListener('click', function (e) {
      const btn = e.target.closest('[data-action="force-octopus"]');
      if (!btn) return;
      // 演示开关：在「二选一」与「三选一」之间切换。
      // 无论三候选来自 20% 概率还是强制触发，回落时都统一回到二候选，
      // 并同步清掉界面上已失效的选择。
      if (pending.candidates.length >= 3) {
        pending.candidates = previewWall(2);
        pending.triggered = false;
        pending.forced = false;
        pending.forceDemo = false;
        if (pending.picked && pending.picked !== 'original' &&
          parseInt(pending.picked.slice(1), 10) >= pending.candidates.length) {
          pending.picked = null;   // 已选中的第 3 张不再存在，需重新选择
        }
        toast('演示：已回到二选一。', 'warn');
        logLine('演示：撤销三候选，回到二选一。', 'trait');
      } else if (round.wall.length >= 3) {
        pending.candidates = previewWall(3);
        pending.triggered = true;
        pending.forced = true;
        pending.forceDemo = true;
        toast('演示：已强制触发章鱼三候选（概率文案仍为 20%）。', 'trait');
        logLine('演示：强制触发章鱼三候选（仍只保留一张）。', 'trait');
      } else {
        toast('牌山不足 3 张，无法演示三候选。', 'warn');
      }
      renderBody();
    });

    renderBody();
  }

  function confirmSleeveBloom() {
    const round = state.round;
    if (!round) return;
    const pending = round.pendingBloom;
    if (!pending || pending.picked === null) { toast('请先选择要保留的牌。', 'warn'); return; }

    const char = currentChar();
    let refund = 0;
    let kept = pending.original;
    const keepOriginal = pending.picked === 'original';
    if (keepOriginal) {
      kept = pending.original;
      if (!kept) { toast('请先选择要保留的牌。', 'warn'); return; }
      if (char.id === 'penguin') refund = 3;
    } else {
      const idx = parseInt(pending.picked.slice(1), 10);
      kept = pending.candidates[idx];
      // 候选与界面必须一致：UI 已显示第 N 张候选时，待确认数组里必须真的有它
      if (!kept) { toast('候选已变化，请重新选择。', 'warn'); return; }
    }

    // ---- 有效性检查（只读，不提交任何折扣标记）----
    const preview = resolveCost('sleeveBloom');
    const net = Math.max(0, preview.cost - refund);
    if (round.points < net) { toast('出千点不足。', 'warn'); return; }
    let widx = -1;
    if (!keepOriginal) {
      widx = round.wall.findIndex(function (t) { return t.u === kept.u; });
      if (widx < 0) { toast('牌山已变化，请重新施术。', 'warn'); return; }
    }

    // ---- 全部检查通过后才提交：此时才消费猴子的一次性折扣 ----
    const priced = resolveCost('sleeveBloom', true);

    // 归属转移：仅在确认时发生，每张实体最终只归属一处
    if (!keepOriginal) {
      // 选中的候选实体离开牌山，成为新的摸牌；原摸牌回到牌山
      round.wall.splice(widx, 1);
      if (round.drawn) round.wall.push(round.drawn);
      round.drawn = kept;
    }
    // 保留原摸牌：候选本就只是预览，牌山与手牌都不变动

    round.points -= net;
    round.usedCheats.push('sleeveBloom');
    round.ledger = round.ledger || [];
    round.ledger.push({
      tech: '袖里藏花', cost: net,
      trait: priced.notes.concat(refund ? ['企鹅 · 保留原摸牌返还 3 点'] : []).join('；') || '—'
    });
    round.traitLog = round.traitLog || [];

    const notes = priced.notes.slice();
    if (refund) notes.push('企鹅 · 保留原摸牌返还 3 点');
    if (pending.triggered) notes.unshift('章鱼 · 20% 概率触发三候选（仍只保留一张）');
    if (notes.length) {
      notes.forEach(function (n) { round.traitLog.push(n); });
      showBanner(char.id, notes.join('；'));
      toast('角色特性触发：' + notes.join('；'), 'trait');
      logLine('角色特性触发 · ' + notes.join('；'), 'trait');
      const card = $('#trait-card');
      if (card) { card.classList.remove('is-triggered'); void card.offsetWidth; card.classList.add('is-triggered'); }
    }

    logLine('袖里藏花完成：保留 ' + tileInfo(kept.c).name + '，消耗 ' + net + ' 点。', 'cheat');
    round.pendingBloom = null;
    closeModal();
    renderTable();
    saveSession();
    announce('袖里藏花完成，保留 ' + tileInfo(kept.c).name + '，出千点剩余 ' + round.points + '。');
    toast('袖里藏花成功：保留 ' + tileInfo(kept.c).name + '（-' + net + ' 点）。', 'cheat');
    const btn = $('[data-action="open-cheat"][data-cheat="sleeveBloom"]');
    if (btn) { btn.classList.remove('is-casting'); void btn.offsetWidth; btn.classList.add('is-casting'); }
  }

  function openBeamSwapModal() {
    const round = state.round;
    if (!round) return;
    const av = cheatAvailability('beamSwap');
    if (!av.ok) { toast(av.why, 'warn'); return; }

    const char = currentChar();
    const hasFox = char.id === 'fox';
    const slotCount = hasFox ? 4 : 3;
    // 暗槽同样只做预览：不从牌山移出实体，确认时才发生一换一的归属转移
    // 待确认状态由同一个 pending 对象承载，UI 与 state.round.pendingSwap 不会脱节
    const pending = {
      slots: previewWall(slotCount),
      pickedHand: null,   // 选中的手牌实体编号
      pickedSlot: null    // 选中的暗槽序号
    };
    const foxPeekIndex = hasFox ? pending.slots.length - 1 : -1;

    const body = document.createElement('div');
    const foot = document.createElement('div');
    foot.innerHTML =
      '<button type="button" class="btn btn--ghost" data-action="close-modal">取消</button>' +
      '<button type="button" class="btn btn--primary" data-action="confirm-beam-swap" disabled>确认交换</button>';

    function renderBody() {
      const priced = resolveCost('beamSwap');
      const handPicks = round.hand.map(function (inst) {
        return '<button type="button" class="pick" role="radio" aria-checked="' +
          (pending.pickedHand === inst.u ? 'true' : 'false') + '" data-action="pick-swap-hand" data-uid="' + inst.u + '">' +
          tileHTML(inst.c, { selected: pending.pickedHand === inst.u }) +
          '<span class="pick__label">' + esc(tileInfo(inst.c).name) + '</span></button>';
      }).join('');

      const slotPicks = pending.slots.map(function (inst, i) {
        const peeked = i === foxPeekIndex;
        return '<button type="button" class="pick" role="radio" aria-checked="' +
          (pending.pickedSlot === i ? 'true' : 'false') + '" data-action="pick-swap-slot" data-index="' + i + '">' +
          (peeked ? tileHTML(inst.c, { selected: pending.pickedSlot === i }) : backTileHTML('tile--sm')) +
          '<span class="pick__label">' + (peeked ? '候选 ' + (i + 1) + '（狐狸预览）' : '候选 ' + (i + 1)) + '</span></button>';
      }).join('');

      const pickedHandTile = pending.pickedHand === null ? null : findTileByUid(round, pending.pickedHand);

      body.innerHTML =
        '<div class="cast-legend"><span class="cast-legend__glyph" aria-hidden="true">换</span>' +
        '<span>偷梁换柱 · 手牌一张 ⇄ 牌山暗槽一张，双方各一张，牌总数严格守恒。</span></div>' +
        costPreviewHTML(priced) +
        '<p class="note">第 1 步选一张<b>手牌</b>，第 2 步选牌山上的一个<b>暗槽</b>。' +
        '双方各一张，牌总数严格守恒；暗槽在确认之前只是<b>预览</b>，取消或关闭不会丢牌。</p>' +
        '<div class="hand-head"><span class="hand-head__title">第 1 步 · 选择手牌</span>' +
        '<span>' + (pickedHandTile ? '已选 ' + esc(tileInfo(pickedHandTile.c).name) : '未选择') + '</span></div>' +
        '<div class="pick-grid pick-grid--swap">' + handPicks + '</div>' +
        '<div class="swap-flow" aria-hidden="true"><span class="swap-flow__arrow"></span>' +
        '<span class="swap-flow__label">一换一 · 等额互换</span>' +
        '<span class="swap-flow__arrow"></span></div>' +
        '<div class="hand-head"><span class="hand-head__title">第 2 步 · 选择牌山暗槽</span>' +
        '<span>' + (pending.pickedSlot !== null ? '已选 候选 ' + (pending.pickedSlot + 1) : '未选择') + '</span></div>' +
        '<div class="pick-grid pick-grid--swap">' + slotPicks + '</div>' +
        (hasFox
          ? '<p class="note note--jade">狐狸特性：偷梁换柱可<b>额外预览一个合法候选</b>（第 ' + slotCount + ' 个槽位已亮出牌面），' +
            '但交换数量仍然是一换一，物理守恒不变。</p>'
          : '<p class="note">候选暗槽默认不露牌面；落汗命中的暗槽才会亮出牌面。</p>') +
        '<p class="note note--warn">演示提示：真实游戏中偷梁换柱立直后禁止；超时摸切费用不退。</p>';
      const confirmBtn = $('[data-action="confirm-beam-swap"]', foot);
      if (confirmBtn) confirmBtn.disabled = !(pending.pickedHand !== null && pending.pickedSlot !== null);
    }

    const overlay = openModal({
      title: '偷梁换柱 · BEAM_SWAP',
      sub: '手牌与牌山暗槽一换一（演示模拟）',
      body: '<div id="bs-body"></div>',
      foot: ''
    });
    const modalBody = $('#bs-body', overlay).parentNode;
    modalBody.appendChild(body);
    foot.style.display = 'contents';
    $('.modal__foot', overlay).appendChild(foot);
    // openModal 内部会先 closeModal() 清空待确认状态，因此这里在弹窗就绪后再挂上
    state.round.pendingSwap = pending;

    body.addEventListener('click', function (e) {
      const handBtn = e.target.closest('[data-action="pick-swap-hand"]');
      if (handBtn) {
        pending.pickedHand = parseInt(handBtn.getAttribute('data-uid'), 10);
        renderBody();
        return;
      }
      const slotBtn = e.target.closest('[data-action="pick-swap-slot"]');
      if (slotBtn) {
        pending.pickedSlot = parseInt(slotBtn.getAttribute('data-index'), 10);
        renderBody();
      }
    });

    renderBody();
  }

  function confirmBeamSwap() {
    const round = state.round;
    if (!round) return;
    const pending = round.pendingSwap;
    if (!pending || pending.pickedHand === null || pending.pickedSlot === null) {
      toast('请先选择手牌与暗槽。', 'warn');
      return;
    }

    const swappedIn = pending.slots[pending.pickedSlot];
    if (!swappedIn) { toast('候选已变化，请重新选择。', 'warn'); return; }
    const handIdx = round.hand.findIndex(function (t) { return t.u === pending.pickedHand; });
    if (handIdx < 0) { toast('请先选择手牌与暗槽。', 'warn'); return; }
    const swappedOut = round.hand[handIdx];

    // ---- 有效性检查（只读，不提交任何折扣标记）----
    const preview = resolveCost('beamSwap');
    if (round.points < preview.cost) { toast('出千点不足。', 'warn'); return; }
    // 归属转移的可行性：暗槽实体必须仍在牌山中
    const widx = round.wall.findIndex(function (t) { return t.u === swappedIn.u; });
    if (widx < 0) { toast('牌山已变化，请重新施术。', 'warn'); return; }

    // ---- 全部检查通过后才提交：此时才消费猴子的一次性折扣 ----
    const priced = resolveCost('beamSwap', true);

    // 归属转移：暗槽实体离开牌山并换入手牌，被换出的手牌回到牌山
    // （牌山数量净变化为 0，手牌数量不变 —— 物理守恒）
    round.wall.splice(widx, 1);
    round.hand[handIdx] = swappedIn;
    round.wall.push(swappedOut);

    round.points -= priced.cost;
    round.usedCheats.push('beamSwap');
    round.ledger = round.ledger || [];
    round.ledger.push({ tech: '偷梁换柱', cost: priced.cost, trait: priced.notes.join('；') || '—' });
    round.traitLog = round.traitLog || [];

    if (priced.notes.length) {
      priced.notes.forEach(function (n) { round.traitLog.push(n); });
      showBanner(currentChar().id, priced.notes.join('；'));
      toast('角色特性触发：' + priced.notes.join('；'), 'trait');
      logLine('角色特性触发 · ' + priced.notes.join('；'), 'trait');
      const card = $('#trait-card');
      if (card) { card.classList.remove('is-triggered'); void card.offsetWidth; card.classList.add('is-triggered'); }
    }

    logLine('偷梁换柱完成：' + tileInfo(swappedOut.c).name + ' ⇄ ' + tileInfo(swappedIn.c).name +
      '，消耗 ' + priced.cost + ' 点。', 'cheat');
    round.pendingSwap = null;
    closeModal();
    renderTable();
    saveSession();
    announce('偷梁换柱完成，消耗 ' + priced.cost + ' 出千点。');
    toast('偷梁换柱成功（-' + priced.cost + ' 点）。', 'cheat');
    const btn = $('[data-action="open-cheat"][data-cheat="beamSwap"]');
    if (btn) { btn.classList.remove('is-casting'); void btn.offsetWidth; btn.classList.add('is-casting'); }
  }

  /* ==========================================================
     12. 回合推进（演示）
     ========================================================== */

  function beginMyTurn() {
    const round = state.round;
    if (!round || state.screen !== 'table') return;
    if (round.wall.length === 0) {
      logLine('牌山已空，本局流局（演示）。', 'warn');
      navigate('settlement', { push: false });
      return;
    }
    // 正常出牌后手牌恒为 13 张：刚摸的那张牌已在出牌时并入手牌（见 discardTile），
    // 这里直接摸新牌即可，牌山只减少一张、实体总数不变。
    round.drawn = round.wall.shift();
    round.phase = 'myTurn';
    round.turnSeat = 0;
    round.selected = null;
    renderTable();
    announce('轮到你，已摸牌 ' + tileInfo(round.drawn.c).name + '。');
    if (round.trust) {
      later(function () {
        if (state.screen !== 'table' || !state.round) return;
        const inst = state.round.drawn;
        if (inst) discardTile(inst.u, true);
      }, 900);
    }
  }

  // 按物理实体编号打出一张牌（手牌或刚摸到的那张）
  function discardTile(uid, isAuto) {
    const round = state.round;
    if (!round || uid === null || uid === undefined) return;
    let inst = null;
    if (round.drawn && round.drawn.u === uid) {
      // 打出刚摸到的那张：手牌不变，仍为 13 张
      inst = round.drawn;
      round.drawn = null;
    } else {
      // 打出普通手牌：刚摸的那张并入手牌，保证出牌后手牌恒为 13 张
      const idx = round.hand.findIndex(function (t) { return t.u === uid; });
      if (idx < 0) return;
      inst = round.hand.splice(idx, 1)[0];
      if (round.drawn) {
        round.hand.push(round.drawn);
        round.drawn = null;
        round.hand = sortHand(round.hand);
      }
    }

    const marked = round.marks.indexOf(inst.u) >= 0;
    round.discards.push({ inst: inst, seat: 0, marked: marked });
    round.selected = null;
    round.phase = 'opponents';
    round.turnSeat = 1;

    logLine((isAuto ? '托管自动打出 ' : '你打出 ') + tileInfo(inst.c).name + (marked ? '（落汗标记牌）' : ''), '');
    renderTable();
    announce('已打出 ' + tileInfo(inst.c).name + '。');
    playOpponentTurns();
  }

  function playOpponentTurns() {
    const round = state.round;
    if (!round) return;
    const order = [1, 2, 3];
    order.forEach(function (seat, i) {
      later(function () {
        if (state.screen !== 'table' || !state.round) return;
        const r = state.round;
        const s = r.seats[seat];
        if (r.wall.length === 0) {
          logLine('牌山已空，本局流局（演示）。', 'warn');
          navigate('settlement', { push: false });
          return;
        }
        const draw = r.wall.shift();
        s.hand.push(draw);
        const discard = s.hand.splice(Math.floor(Math.random() * s.hand.length), 1)[0];
        r.discards.push({ inst: discard, seat: seat, marked: false });
        r.turnSeat = seat;
        logLine(s.name + ' 打出 ' + tileInfo(discard.c).name + '。', '');
        renderTable();
      }, 620 * (i + 1));
    });
    later(function () {
      if (state.screen !== 'table' || !state.round) return;
      beginMyTurn();
    }, 620 * order.length + 620);
  }

  /* ==========================================================
     13. 房间流程
     ========================================================== */

  function createRoom(mode) {
    const oppChars = shuffle(CHARACTERS.map(function (c) { return c.id; }), 20260101).slice(0, 3);
    const seats = [];
    const labels = ['东', '南', '西', '北'];
    for (let i = 0; i < 4; i++) {
      seats.push({
        seat: i,
        seatLabel: labels[i] + '家',
        name: i === 0 ? '我（' + currentChar().name + '）' : DEMO_NAMES[i],
        charId: i === 0 ? state.characterId : oppChars[i - 1],
        isMe: i === 0,
        ready: i === 0 ? false : true
      });
    }
    state.room = {
      id: mode === 'online' ? 'MJ-' + Math.random().toString(36).slice(2, 6).toUpperCase() : 'SOLO-DEMO',
      mode: mode,
      modeText: mode === 'online' ? '东风战（4 人）· 演示模拟数据' : '单机东风战 · 演示模拟数据',
      seats: seats,
      stackSlot: null,
      stackPaid: false,
      stackSlots: DEFAULT_STACK_SLOTS.slice()
    };
    saveSession();
    navigate('room');
    announce('房间已创建，正在准备中。');
  }

  /* ==========================================================
     14. 事件绑定
     ========================================================== */

  function onAction(action, el) {
    const round = state.round;

    switch (action) {
      case 'goto-mode': navigate('mode'); break;
      case 'goto-lobby': navigate('lobby'); break;
      case 'goto-characters': navigate('characters'); break;
      case 'goto-codex': navigate('codex'); break;
      case 'goto-menu': navigate('menu', { push: false }); state.navStack = []; break;
      case 'open-settings': navigate('settings'); break;
      case 'back': goBack(); break;

      case 'solo-quick':
        state.mode = 'solo';
        navigate('characters');
        break;
      case 'mode-solo':
        state.mode = 'solo';
        navigate('characters');
        break;
      case 'mode-online':
        state.mode = 'online';
        navigate('lobby');
        break;

      case 'select-character': {
        const id = el.getAttribute('data-id');
        if (state.locked[id]) {
          toast('「' + CHAR_BY_ID[id].name + '」已被锁定，请先解锁。', 'warn');
          announce('该角色已锁定，无法选用。');
          return;
        }
        state.characterId = id;
        if (round && round.seats[0]) {
          round.seats[0].charId = id;
          round.seats[0].name = '我（' + CHAR_BY_ID[id].name + '）';
        }
        RENDERERS.characters();
        saveSession();
        announce('已选择角色 ' + CHAR_BY_ID[id].name + '，' + CHAR_BY_ID[id].trait + '。');
        break;
      }
      case 'toggle-lock': {
        const id = el.getAttribute('data-id');
        state.locked[id] = !state.locked[id];
        if (state.locked[id] && state.characterId === id) {
          // 锁定当前角色：保持选中但确认按钮禁用，并给出提示
          toast('已锁定「' + CHAR_BY_ID[id].name + '」，确认按钮暂时不可用。', 'warn');
        }
        RENDERERS.characters();
        saveSession();
        announce((state.locked[id] ? '已锁定' : '已解锁') + '角色 ' + CHAR_BY_ID[id].name + '。');
        break;
      }
      case 'confirm-character': {
        const id = state.characterId;
        if (state.locked[id]) { toast('该角色已锁定，无法确认。', 'warn'); return; }
        if (state.mode === 'online') {
          navigate('lobby');
        } else {
          createRoom('solo');
        }
        announce('已确认使用角色 ' + CHAR_BY_ID[id].name + '。');
        break;
      }

      case 'refresh-rooms':
        toast('房间列表已刷新（演示模拟数据）。');
        RENDERERS.lobby();
        break;
      case 'open-create-room':
        createRoom('online');
        break;
      case 'open-join-room':
        openJoinRoomDialog();
        break;
      case 'join-room': {
        const id = el.getAttribute('data-room');
        const room = DEMO_ROOMS.filter(function (r) { return r.id === id; })[0];
        if (!room) return;
        createRoom('online');
        state.room.id = room.id;
        state.room.modeText = room.mode + '（' + room.players + ' / ' + room.cap + ' 人）· 演示模拟数据';
        RENDERERS.room();
        announce('已加入房间 ' + room.id + '（演示模拟数据）。');
        break;
      }
      case 'spectate-room': {
        const id = el.getAttribute('data-room');
        toast('观战「' + id + '」为演示示意流程，未建立真实观战连接。', 'warn');
        break;
      }

      case 'toggle-ready': {
        if (!state.room) return;
        const me = state.room.seats.filter(function (s) { return s.isMe; })[0];
        if (!me) return;
        me.ready = !me.ready;
        RENDERERS.room();
        saveSession();
        announce(me.ready ? '你已准备。' : '你已取消准备。');
        break;
      }
      case 'start-match': {
        if (!state.room) return;
        if (!state.room.seats.every(function (s) { return s.ready; })) {
          toast('仍有席位未准备。', 'warn');
          return;
        }
        state.round = createRound(Date.now() & 0x7fffffff);
        state.round.ledger = [];
        state.round.traitLog = [];
        state.round.phase = 'myTurn';
        saveSession();
        navigate('table');
        announce('对局开始，轮到你出牌。');
        break;
      }
      case 'leave-room':
        state.room = null;
        navigate('lobby', { push: false });
        toast('已离开房间（演示）。');
        break;
      case 'select-stack': {
        if (!state.room || state.room.stackPaid) return;
        const i = parseInt(el.getAttribute('data-index'), 10);
        state.room.stackSlot = i;
        RENDERERS.room();
        break;
      }
      case 'confirm-stack': {
        if (!state.room || state.room.stackPaid) return;
        if (state.room.stackSlot === null) { toast('请先选择一个码牌目标。', 'warn'); return; }
        state.room.stackPaid = true;
        RENDERERS.room();
        toast('码牌已生效，消耗 30 出千点（演示）。', 'cheat');
        announce('码牌已确认：' + state.room.stackSlots[state.room.stackSlot].label + '。');
        break;
      }

      case 'open-cheat': {
        const tech = el.getAttribute('data-cheat');
        if (tech === 'tileMark') openTileMarkModal();
        else if (tech === 'sleeveBloom') openSleeveBloomModal();
        else if (tech === 'beamSwap') openBeamSwapModal();
        break;
      }
      case 'close-modal': closeModal(); break;
      case 'confirm-tile-mark': confirmTileMark(); break;
      case 'confirm-sleeve-bloom': confirmSleeveBloom(); break;
      case 'confirm-beam-swap': confirmBeamSwap(); break;

      case 'select-tile': {
        if (!round) return;
        const uid = parseInt(el.getAttribute('data-uid'), 10);
        if (isNaN(uid)) return;
        round.selected = round.selected === uid ? null : uid;
        renderHand();
        renderActionStack();
        const sel = round.selected === null ? null : findTileByUid(round, round.selected);
        announce(sel ? '已选中 ' + tileInfo(sel.c).name : '已取消选牌。');
        break;
      }
      case 'discard': {
        if (!round || !round.selected) { toast('请先选择一张手牌。', 'warn'); return; }
        discardTile(round.selected, false);
        break;
      }
      case 'sort-hand': {
        if (!round) return;
        round.hand = sortHand(round.hand);
        logLine('理牌完成，手牌已排序。', '');
        renderHand();
        toast('已理牌。');
        break;
      }
      case 'toggle-trust': {
        if (!round) return;
        round.trust = !round.trust;
        renderActionStack();
        renderTableMeta();
        renderCheatStack();   // 托管会影响可用性判断（如施术窗口），同步刷新按钮与禁用原因
        logLine(round.trust ? '已开启托管，出牌将自动进行。' : '已取消托管。', round.trust ? 'warn' : '');
        announce(round.trust ? '托管已开启。' : '托管已关闭。');
        break;
      }
      case 'add-points': {
        if (!round) return;
        round.points += 50;
        logLine('补充演示点数 +50（仅演示，不是真实游戏货币）。', 'warn');
        renderTable();
        saveSession();
        toast('已补充 50 点演示出千点。', 'warn');
        break;
      }
      case 'settle-now':
        if (!round) return;
        navigate('settlement');
        break;
      case 'restart-round': {
        if (!state.room) return;
        state.round = createRound(Date.now() & 0x7fffffff);
        state.round.ledger = [];
        state.round.traitLog = [];
        state.round.phase = 'myTurn';
        logLine('重开本局（演示）。', 'warn');
        renderTable();
        saveSession();
        toast('已重开本局。', 'warn');
        break;
      }
      case 'replay-round': {
        state.round = createRound(Date.now() & 0x7fffffff);
        state.round.ledger = [];
        state.round.traitLog = [];
        state.round.phase = 'myTurn';
        saveSession();
        navigate('table', { push: false });
        toast('已重新开局（角色与设置保持）。', 'cheat');
        break;
      }
      case 'resume-table':
        if (!state.round) { toast('没有可继续的牌局。', 'warn'); return; }
        state.round.phase = 'myTurn';
        navigate('table');
        break;
      case 'restart-demo':
        resetDemo();
        navigate('menu', { push: false });
        break;

      case 'set-theme': {
        state.settings.theme = el.getAttribute('data-value');
        applySettings();
        RENDERERS.settings();
        saveSession();
        announce('牌面皮肤已切换为' + (THEMES.filter(function (t) { return t.id === state.settings.theme; })[0] || {}).name + '。');
        break;
      }
      case 'set-motion': {
        state.settings.motion = el.getAttribute('data-value');
        applySettings();
        RENDERERS.settings();
        saveSession();
        announce(state.settings.motion === 'reduced' ? '已关闭动画。'
          : state.settings.motion === 'full' ? '已恢复完整动效。'
            : '动效强度改为跟随系统偏好。');
        break;
      }
      case 'set-textsize': {
        state.settings.textsize = el.getAttribute('data-value');
        applySettings();
        RENDERERS.settings();
        saveSession();
        break;
      }
      case 'reset-demo':
        resetDemo();
        RENDERERS.settings();
        toast('演示数据已重置。', 'warn');
        break;

      case 'tab': {
        const tab = el.getAttribute('data-tab');
        $$('.tab').forEach(function (t) {
          const on = t.getAttribute('data-tab') === tab;
          t.classList.toggle('is-active', on);
          t.setAttribute('aria-selected', on ? 'true' : 'false');
        });
        $$('.tab-panel').forEach(function (p) {
          const on = p.getAttribute('id') === 'panel-' + tab;
          p.classList.toggle('is-active', on);
          if ('inert' in p) p.inert = !on;
          p.setAttribute('aria-hidden', on ? 'false' : 'true');
        });
        break;
      }
      default: break;
    }
  }

  function openJoinRoomDialog() {
    const body = document.createElement('div');
    body.innerHTML =
      '<div class="field"><label class="field__label" for="join-room-id">房间号</label>' +
      '<input type="text" id="join-room-id" placeholder="例如 MJ-7K2Q" value="MJ-3D8X" data-autofocus /></div>' +
      '<p class="note note--warn">演示提示：此处不会发起任何真实连接，输入房间号后直接进入本地模拟的房间准备界面。</p>';
    const foot = document.createElement('div');
    foot.innerHTML =
      '<button type="button" class="btn btn--ghost" data-action="close-modal">取消</button>' +
      '<button type="button" class="btn btn--primary" data-action="confirm-join-room">加入房间</button>';

    const overlay = openModal({ title: '按房间号加入', sub: '演示模拟数据', body: '', foot: '' });
    const modalBody = $('.modal__body', overlay);
    modalBody.appendChild(body);
    foot.style.display = 'contents';
    $('.modal__foot', overlay).appendChild(foot);

    foot.addEventListener('click', function (e) {
      if (!e.target.closest('[data-action="confirm-join-room"]')) return;
      const id = ($('#join-room-id') && $('#join-room-id').value || '').trim();
      if (!id) { toast('请输入房间号。', 'warn'); return; }
      closeModal();
      createRoom('online');
      state.room.id = id;
      state.room.modeText = '自定义房间 ' + id + ' · 演示模拟数据';
      RENDERERS.room();
      announce('已加入房间 ' + id + '（演示模拟数据）。');
    });
  }

  function resetDemo() {
    state.round = null;
    state.room = null;
    state.ledger = [];
    state.locked = {};
    state.mode = null;
    try { localStorage.removeItem(STORAGE_KEY); } catch (err) { /* ignore */ }
    applySettings();
    RENDERERS.menu();
  }

  function applySettings() {
    const html = document.documentElement;
    html.setAttribute('data-theme', state.settings.theme);
    html.setAttribute('data-motion', effectiveMotion());
    html.setAttribute('data-textsize', state.settings.textsize);
  }

  /** 系统是否要求降低动态效果。 */
  function systemPrefersReducedMotion() {
    try {
      return !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
    } catch (err) { return false; }
  }

  /**
   * 实际生效的动效强度：
   * auto → 跟随系统 prefers-reduced-motion；full / reduced → 用户显式指定优先。
   */
  function effectiveMotion() {
    if (state.settings.motion === 'reduced') return 'reduced';
    if (state.settings.motion === 'full') return 'full';
    return systemPrefersReducedMotion() ? 'reduced' : 'full';
  }

  /** 监听系统动效偏好变化：处于「跟随系统」时立即生效。 */
  function watchMotionPreference() {
    try {
      if (!window.matchMedia) return;
      const mq = window.matchMedia('(prefers-reduced-motion: reduce)');
      const onChange = function () {
        if (state.settings.motion !== 'auto') return;
        applySettings();
        RENDERERS.settings();
        announce(systemPrefersReducedMotion() ? '已跟随系统偏好关闭动效。' : '已跟随系统偏好恢复动效。');
      };
      if (mq.addEventListener) mq.addEventListener('change', onChange);
      else if (mq.addListener) mq.addListener(onChange);
    } catch (err) { /* 忽略不支持的环境 */ }
  }

  /* ---------- 全局事件委托 ---------- */

  document.addEventListener('click', function (e) {
    const el = e.target.closest('[data-action]');
    if (!el) return;
    if (el.disabled) return;
    onAction(el.getAttribute('data-action'), el);
  });

  document.addEventListener('change', function (e) {
    if (e.target && e.target.id === 'setting-octopus-demo') {
      state.settings.octopusDemo = !!e.target.checked;
      saveSession();
      announce(state.settings.octopusDemo
        ? '已开启章鱼三候选强制演示，概率文案仍为 20%。'
        : '已关闭章鱼三候选强制演示，恢复 20% 概率。');
    }
    if (e.target && e.target.id === 'lobby-auto-refresh') {
      toast(e.target.checked ? '自动刷新已开启（演示模拟）。' : '自动刷新已关闭。', 'warn');
    }
  });

  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') {
      if (modalEl) return; // 由模态自己处理
      if (state.screen !== 'menu') {
        e.preventDefault();
        goBack();
      }
      return;
    }
    if (e.key === 'Enter' && state.screen === 'table' && state.round && state.round.selected &&
      !modalEl && document.activeElement && document.activeElement.tagName === 'BUTTON' &&
      document.activeElement.getAttribute('data-action') === 'select-tile') {
      e.preventDefault();
      discardTile(state.round.selected, false);
    }
  });

  /* ==========================================================
     15. 启动
     ========================================================== */

  // 只读调试句柄：供原型评审与自动化检查查看当前演示状态，不提供任何写入能力。
  window.__MJ_PROTO__ = {
    getState: function () { return state; },
    getRound: function () { return state.round; },
    countTiles: function () {
      const r = state.round;
      if (!r) return 0;
      const opp = r.seats.reduce(function (a, s) { return a + (s.isMe ? 0 : s.hand.length); }, 0);
      const meld = r.melds.reduce(function (a, m) { return a + m.codes.length; }, 0);
      return r.hand.length + (r.drawn ? 1 : 0) + r.wall.length + r.discards.length + opp + meld;
    }
  };

  function init() {
    loadSession();
    applySettings();
    watchMotionPreference();
    document.body.setAttribute('data-screen', 'menu');
    RENDERERS.menu();
    RENDERERS.settings();
    RENDERERS.codex();
    RENDERERS.lobby();
    RENDERERS.characters();
    // 初始显示主菜单
    $$('.screen').forEach(function (sec) {
      const on = sec.getAttribute('data-screen') === 'menu';
      sec.classList.toggle('is-active', on);
      if ('inert' in sec) sec.inert = !on;
      sec.setAttribute('aria-hidden', on ? 'false' : 'true');
    });
    const menuTitle = $('#menu-title');
    if (menuTitle) menuTitle.focus({ preventScroll: true });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
