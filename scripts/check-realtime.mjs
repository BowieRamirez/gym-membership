// Live check of phase 4c against the real project. Needs three accounts and writes ONE real message to the front desk thread of account A:
//   A = a member (sends the message), B = front desk staff (must receive the change), C = a different member (must NOT receive it).
//   SUPABASE_URL SUPABASE_ANON_KEY  A_EMAIL A_PASSWORD  B_EMAIL B_PASSWORD  C_EMAIL C_PASSWORD   node scripts/check-realtime.mjs
// Secrets are never printed.
const base = (process.env.SUPABASE_URL ?? '').replace(/\/+$/, ''), key = process.env.SUPABASE_ANON_KEY ?? '';
const need = ['A', 'B', 'C'].flatMap(x => [`${x}_EMAIL`, `${x}_PASSWORD`]);
if (!base || !key || need.some(n => !process.env[n])) { console.error('Set SUPABASE_URL, SUPABASE_ANON_KEY and ' + need.join(', ')); process.exit(2); }

const signIn = async x => {
  const r = await fetch(`${base}/auth/v1/token?grant_type=password`, { method: 'POST', headers: { apikey: key, 'Content-Type': 'application/json' },
    body: JSON.stringify({ email: process.env[`${x}_EMAIL`], password: process.env[`${x}_PASSWORD`] }) });
  const j = await r.json(); if (!j.access_token) throw new Error(`${x} sign-in failed (${r.status}: ${j.error_description ?? j.msg ?? 'no reason given'})`);
  return { token: j.access_token, id: j.user.id };
};
const listen = (who, topic) => new Promise((resolve, reject) => {
  const ws = new WebSocket(base.replace(/^http/, 'ws') + `/realtime/v1/websocket?apikey=${key}&vsn=1.0.0`);
  const seen = []; let ready = false;
  ws.onmessage = e => {
    const m = JSON.parse(e.data);
    if (m.event === 'phx_reply' && m.ref === '1') { if (m.payload.status === 'ok') { ready = true; resolve({ ws, seen }); } else reject(new Error(`join refused: ${JSON.stringify(m.payload).slice(0, 160)}`)); }
    if (m.event === 'postgres_changes') seen.push(m.payload.data?.table);
  };
  ws.onopen = () => ws.send(JSON.stringify({ topic, event: 'phx_join', ref: '1', payload: { access_token: who.token, config: {
    postgres_changes: [{ event: '*', schema: 'public', table: 'messages' }, { event: '*', schema: 'public', table: 'support_messages' }] } } }));
  setTimeout(() => !ready && reject(new Error('join timed out')), 10000);
});

const [A, B, C] = await Promise.all(['A', 'B', 'C'].map(signIn));
const [lb, lc] = await Promise.all([listen(B, 'realtime:check-b'), listen(C, 'realtime:check-c')]);
await new Promise(r => setTimeout(r, 1500));   // let the subscriptions settle
const sentAt = Date.now();
const res = await fetch(`${base}/rest/v1/rpc/send_support_message`, { method: 'POST', headers: { apikey: key, Authorization: `Bearer ${A.token}`, 'Content-Type': 'application/json' },
  body: JSON.stringify({ p_member: A.id, p_body: 'realtime check ' + new Date().toISOString() }) });
if (!res.ok) { console.log('FAIL  send_support_message', res.status, (await res.text()).slice(0, 160)); process.exit(1); }
await new Promise(r => setTimeout(r, 3000));
let failed = 0;
const check = (name, ok, detail = '') => { console.log(`${ok ? 'ok   ' : 'FAIL '} ${name}${detail}`); if (!ok) failed++; };
check('front desk (B) is told about the new support message', lb.seen.includes('support_messages'), ` (seen: ${lb.seen.join(',') || 'nothing'})`);
check('an unrelated member (C) is told nothing', lc.seen.length === 0, ` (seen: ${lc.seen.join(',') || 'nothing'})`);
console.log(`message sent at +0 ms; listened for 3000 ms (started ${Date.now() - sentAt} ms ago)`);
lb.ws.close(); lc.ws.close();
process.exit(failed ? 1 : 0);
