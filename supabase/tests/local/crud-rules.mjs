// Item 13 (D14, D18-D20): what signed-in users may do to each table, as approved by the owner. Master data can be created, read and
// updated, and "deleted" only by archiving; money and history can't be edited or removed. A new table fails here until it is listed.
import assert from 'node:assert/strict';
import { start } from './harness.mjs';

const { server, client: db } = await start();
let passed = 0, failed = 0, serial = 0;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (error) { failed++; console.log('  FAIL', name, error.message); }
}
async function as(uid, sql, values = []) {
  await db.query('begin');
  try {
    await db.query('set local role authenticated');
    await db.query(`select set_config('request.jwt.claim.sub',$1,true)`, [uid]);
    const result = await db.query(sql, values); await db.query('commit'); return result;
  } catch (error) { await db.query('rollback'); throw error; }
}
const person = async (role = 'member') => (await db.query(
  'insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`crud-${++serial}@test.io`, JSON.stringify({ full_name: 'Crud Person' }), JSON.stringify({ role })])).rows[0].id;

// S select, I insert, U update, D delete; each still limited to the right rows and columns by the row rules and the column grants
const expected = {
  // master data: never D, because delete means archive
  amenities: 'SIU-', discounts: 'SIU-', membership_packages: 'SIU-',
  coaches: 'S-U-', gym_settings: 'S-U-', user_settings: 'S-U-',
  profiles: 'S---', members: 'S---',                      // only server functions change these (D18)
  // day-to-day records the app writes directly
  shift_posts: 'SIU-', user_notifications: 'S-U-',        // cancelled, read; never erased (D20)
  messages: 'SIU-', amenity_usages: 'SI--',
  // money and history: only server functions change these
  payments: 'S---', refund_requests: 'S---', membership_terms: 'S---', membership_renewals: 'S---', user_membership_packages: 'S---',
  user_amenities: 'S---', gym_check_ins: 'S---', member_scans: 'S---', member_codes: 'S---', audit_log: 'S---', session_attendance: 'S---',
  training_sessions: 'S---', time_requests: 'S---', time_request_events: 'S---', reservations: 'S---', coach_hires: 'S---', coach_visits: 'S---',
  support_threads: 'S---', support_messages: 'S---', amenity_follows: 'S---',
  roles: 'S---', permissions: 'S---', role_permissions: 'S---', user_roles: 'S---',
  // server only
  email_checks: '----', user_last_seen: '----',
};

try {
  await test('every table allows signed-in users exactly what the approved table says', async () => {
    const rows = (await db.query(`select c.relname as t,
        has_table_privilege('authenticated',c.oid,'select') as s, has_any_column_privilege('authenticated',c.oid,'insert') as i,
        has_any_column_privilege('authenticated',c.oid,'update') as u, has_table_privilege('authenticated',c.oid,'delete') as d
      from pg_class c join pg_namespace n on n.oid=c.relnamespace where n.nspname='public' and c.relkind='r'`)).rows;
    const actual = Object.fromEntries(rows.map(r => [r.t, (r.s ? 'S' : '-') + (r.i ? 'I' : '-') + (r.u ? 'U' : '-') + (r.d ? 'D' : '-')]));
    assert.deepEqual(Object.fromEntries(Object.keys(actual).sort().map(k => [k, actual[k]])), Object.fromEntries(Object.keys(expected).sort().map(k => [k, expected[k]])));
  });
  await test('anonymous users get nothing on any table', async () => {
    const rows = (await db.query(`select c.relname as t from pg_class c join pg_namespace n on n.oid=c.relnamespace
      where n.nspname='public' and c.relkind='r' and (has_any_column_privilege('anon',c.oid,'select') or has_any_column_privilege('anon',c.oid,'insert')
        or has_any_column_privilege('anon',c.oid,'update') or has_table_privilege('anon',c.oid,'delete'))`)).rows;
    assert.deepEqual(rows, []);
  });
  await test('phone and goals can no longer be edited, and what is stored stays (D18)', async () => {
    const id = await person();
    await db.query(`update profiles set phone='09170000000' where id=$1`, [id]);
    await db.query(`update members set goals='Run 5k' where user_id=$1`, [id]);
    await assert.rejects(as(id, `update profiles set phone='1' where id=$1`, [id]), /permission denied/);
    await assert.rejects(as(id, `update members set goals='x' where user_id=$1`, [id]), /permission denied/);
    assert.equal((await db.query('select phone from profiles where id=$1', [id])).rows[0].phone, '09170000000');
    assert.equal((await db.query('select goals from members where user_id=$1', [id])).rows[0].goals, 'Run 5k');
  });
  await test('a coach can cancel a shift but not erase it, and nobody can erase a notification (D20)', async () => {
    const coach = await person('coach');
    const shift = (await db.query(`insert into shift_posts(coach_id,starts_at,ends_at) values($1,(date_trunc('day',now() at time zone 'Asia/Manila')+interval '2 days 10 hours') at time zone 'Asia/Manila',(date_trunc('day',now() at time zone 'Asia/Manila')+interval '2 days 14 hours') at time zone 'Asia/Manila') returning shift_post_id`, [coach])).rows[0].shift_post_id;
    await assert.rejects(as(coach, 'delete from shift_posts where shift_post_id=$1', [shift]), /permission denied/);
    await as(coach, `update shift_posts set status='cancelled' where shift_post_id=$1`, [shift]);
    assert.equal((await db.query('select status from shift_posts where shift_post_id=$1', [shift])).rows[0].status, 'cancelled');

    await db.query(`insert into user_notifications(user_id,type,title) values($1,'system','Hello')`, [coach]);
    await assert.rejects(as(coach, 'delete from user_notifications where user_id=$1', [coach]), /permission denied/);
    await as(coach, 'update user_notifications set read_at=now() where user_id=$1', [coach]);
  });
  await test('a discount code and percent are fixed; its end date, limit and on/off can change (D19)', async () => {
    const owner = await person('admin');
    const code = (await as(owner, `insert into discounts(code,percent,expires_at,max_uses) values('FIXED10',10,now()+interval '30 days',5) returning discount_id`)).rows[0].discount_id;
    const later = new Date(Date.now() + 60 * 86400000).toISOString();
    await as(owner, 'update discounts set expires_at=$2 where discount_id=$1', [code, later]);
    await as(owner, 'update discounts set max_uses=9 where discount_id=$1', [code]);
    const row = (await db.query('select expires_at,max_uses from discounts where discount_id=$1', [code])).rows[0];
    assert.equal(row.expires_at.toISOString(), later); assert.equal(row.max_uses, 9);

    await assert.rejects(as(owner, `update discounts set code='OTHER' where discount_id=$1`, [code]), /discount_fixed/);
    await assert.rejects(as(owner, 'update discounts set percent=50 where discount_id=$1', [code]), /discount_fixed/);
    // installed apps send every column back on save, so the same values, or the same code in another case, must still pass
    await as(owner, `update discounts set code='fixed10',percent=10,max_uses=8 where discount_id=$1`, [code]);
    assert.equal((await db.query('select max_uses from discounts where discount_id=$1', [code])).rows[0].max_uses, 8);
  });
  await test('master data cannot be physically deleted, even by a system admin', async () => {
    const owner = await person('admin');
    for (const [table, key, sql] of [
      ['membership_packages', 'membership_package_id', `insert into membership_packages(name,price,duration_days) values('Keep plan',1,1) returning membership_package_id as id`],
      ['amenities', 'amenity_id', `insert into amenities(name,price) values('Keep amenity',1) returning amenity_id as id`],
      ['discounts', 'discount_id', `insert into discounts(code,percent,expires_at,max_uses) values('KEEP1',1,now()+interval '1 day',1) returning discount_id as id`]]) {
      const id = (await db.query(sql)).rows[0].id;
      await assert.rejects(as(owner, `delete from ${table} where ${key}=$1`, [id]), /permission denied/);
    }
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`CRUD rules: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
