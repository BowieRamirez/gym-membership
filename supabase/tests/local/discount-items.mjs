// Item 2 of the 2026-11-12 plan (G1 to G4, 0060). A code is made in one step with the packages and amenities it fits and can't be
// edited after (G2); it can be turned off and on, archived, or deleted while no payment has ever used it (G1). Every payment that
// carries a code is checked against what it buys (G3). Members see the shown codes they can still use, with what each fits (G4).
import { start, openCashShifts } from './harness.mjs';
import assert from 'node:assert/strict';

let legacy;
const { server, client: db } = await start({ beforeMigration: async (client, file) => {
  if (file.startsWith('0060_'))   // a live code from before 0060: it must keep fitting everything it fitted
    legacy = (await client.query(`insert into discounts(code,percent,expires_at,max_uses) values('OLDCODE',10,now()+interval '30 days',50) returning discount_id`)).rows[0].discount_id;
} });
let passed = 0, failed = 0, serial = 0;
async function as(uid, sql, args = []) {
  await db.query('begin');
  try {
    await db.query('set local role authenticated');
    await db.query(`select set_config('request.jwt.claim.sub', $1, true)`, [uid]);
    const r = await db.query(sql, args);
    await db.query('commit');
    return r.rows;
  } catch (e) { await db.query('rollback'); throw e; }
}
async function user(role = 'member') {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,'{"full_name":"Test Person"}',$2) returning id`,
    [`g-${++serial}@test.io`, JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}

const admin = await user('admin'), sub = await user('subadmin'), desk = await user('employee'), coach = await user('coach');
await openCashShifts(db);
const pkg = async name => Number((await db.query(`insert into membership_packages(name,price,duration_days) values($1,1000,30) returning membership_package_id`, [name])).rows[0].membership_package_id);
const amen = async name => Number((await db.query(`insert into amenities(name,price,duration_days) values($1,300,30) returning amenity_id`, [name])).rows[0].amenity_id);
const monthly = await pkg('G Monthly'), annual = await pkg('G Annual'), pool = await amen('G Pool'), sauna = await amen('G Sauna');
const until = () => new Date(Date.now() + 30 * 86400000).toISOString();
const create = (who, code, items, { percent = 10, limit = 20, show = true, expires = until() } = {}) =>
  as(who, 'select create_discount($1,$2,$3,$4,$5,$6::jsonb) id', [code, percent, expires, limit, show, JSON.stringify(items)]).then(r => r[0].id);
const row = async c => (await db.query('select * from discounts where code=$1', [c])).rows[0];
const buyPackage = async (uid, p, c) => (await as(uid, 'select * from extend_membership($1,$2)', [p, c]))[0];
const buyAmenity = async (uid, a, c) => (await as(uid, 'select * from avail_amenity($1,$2)', [a, c]))[0];
// amenities are sold to members with an active membership
const member = async () => { const m = await user(); const p = await buyPackage(m, monthly, null); await as(desk, 'select verify_payment($1,true)', [p.payment_id]); return m; };
const codesFor = async uid => (await as(uid, 'select * from my_discount_codes()')).map(r => r.code);

try {
  await test('a code made before 0060 still fits every package and amenity', async () => {
    const items = (await db.query('select kind, item_id from discount_items where discount_id=$1 order by kind', [legacy])).rows;
    assert.deepEqual(items, [{ kind: 'amenity', item_id: null }, { kind: 'package', item_id: null }]);
    assert.ok((await buyAmenity(await member(), pool, 'OLDCODE')).discount_id);
    assert.ok((await buyPackage(await user(), monthly, 'OLDCODE')).discount_id);
  });
  await test('create_discount makes the code and its items together, upper-cased; the audit log keeps it', async () => {
    const id = await create(sub, ' g-month ', [{ kind: 'package', id: monthly }]);
    const d = await row('G-MONTH');
    assert.equal(String(d.discount_id), String(id));
    assert.deepEqual([d.percent, d.max_uses, d.show_on_packages, d.paused, d.is_active], [10, 20, true, false, true]);
    assert.deepEqual((await db.query('select kind, item_id::text from discount_items where discount_id=$1', [id])).rows, [{ kind: 'package', item_id: String(monthly) }]);
    assert.equal((await db.query(`select count(*)::int n from audit_log where action='discount.create' and entity_id=$1`, [String(id)])).rows[0].n, 1);
  });
  await test('create_discount refuses bad input, no items, unknown or archived items, and a taken code', async () => {
    const gone = await pkg('G Gone'); await db.query('update membership_packages set is_active=false where membership_package_id=$1', [gone]);
    for (const [code, items, opts, why] of [
      ['G-NOITEMS', [], {}, /no_items/], ['G-BADKIND', [{ kind: 'coach', id: null }], {}, /bad_input/],
      ['G-NOSUCH', [{ kind: 'package', id: 999999 }], {}, /item_not_found/], ['G-GONE', [{ kind: 'package', id: gone }], {}, /item_not_found/],
      ['G-PCT', [{ kind: 'package', id: null }], { percent: 0 }, /bad_discount/], ['G-PCT2', [{ kind: 'package', id: null }], { percent: 101 }, /bad_discount/],
      ['G-LIMIT', [{ kind: 'package', id: null }], { limit: null }, /limit_required/], ['G-LIMIT0', [{ kind: 'package', id: null }], { limit: 0 }, /limit_required/],
      ['G-PAST', [{ kind: 'package', id: null }], { expires: new Date(Date.now() - 1000).toISOString() }, /bad_discount/],
      ['x', [{ kind: 'package', id: null }], {}, /bad_discount/], ["G'; drop table discounts; --", [{ kind: 'package', id: null }], {}, /bad_discount/],
      ['G-MONTH', [{ kind: 'package', id: null }], {}, /code_taken/]])
      await assert.rejects(create(admin, code, items, opts), e => why.test(e.message), code);
    assert.equal((await db.query(`select count(*)::int n from discounts where code like 'G-NO%' or code like 'G-PCT%'`)).rows[0].n, 0);
  });
  await test('only discount managers create codes; front desk, coach, member and anonymous are refused', async () => {
    for (const who of [desk, coach, await user()]) await assert.rejects(create(who, 'G-NOPE', [{ kind: 'package', id: null }]), /forbidden/);
    await db.query('begin'); await db.query('set local role anon');
    await assert.rejects(db.query(`select create_discount('G-ANON',10,now()+interval '1 day',5,true,'[]')`), /permission denied/);
    await db.query('rollback');
  });
  await test('G2: nobody writes the table directly, so nothing about a code can change after it is made', async () => {
    const id = (await row('G-MONTH')).discount_id;
    for (const sql of ['update discounts set percent=50 where discount_id=$1', 'update discounts set max_uses=99 where discount_id=$1',
                       `update discounts set expires_at=now()+interval '90 days' where discount_id=$1`, 'update discounts set show_on_packages=false where discount_id=$1',
                       'delete from discounts where discount_id=$1'])
      await assert.rejects(as(admin, sql, [id]), /permission denied/);
    await assert.rejects(as(admin, `insert into discounts(code,percent,expires_at,max_uses) values('G-DIRECT',10,now()+interval '1 day',5)`), /permission denied/);
    await assert.rejects(as(admin, `insert into discount_items(discount_id,kind,item_id) values($1,'amenity',null)`, [id]), /permission denied/);
    await assert.rejects(db.query('update discounts set max_uses=99 where discount_id=$1', [id]), /discount_locked/);   // even a privileged write
    assert.equal((await row('G-MONTH')).max_uses, 20);
  });
  await test('G3: a code works only for what it fits, for members and at the Cashier', async () => {
    const m = await member();
    await assert.rejects(buyPackage(m, annual, 'G-MONTH'), /code_not_for_item/);
    await assert.rejects(buyAmenity(m, pool, 'G-MONTH'), /code_not_for_item/);
    await assert.rejects(as(desk, `select record_counter_payment('membership','cash',$1,$2,null,null,'G-MONTH')`, [await user(), annual]), /code_not_for_item/);
    assert.equal((await db.query('select count(*)::int n from payments where member_id=$1 and discount_id is not null', [m])).rows[0].n, 0);   // nothing half-made
    assert.ok((await buyPackage(m, monthly, 'G-MONTH')).discount_id);
    await create(admin, 'G-SAUNA', [{ kind: 'amenity', id: sauna }]);
    await create(admin, 'G-AMEN', [{ kind: 'amenity', id: null }]);
    assert.ok((await buyAmenity(await member(), sauna, 'G-SAUNA')).discount_id);
    await assert.rejects(buyAmenity(await member(), pool, 'G-SAUNA'), /code_not_for_item/);
    assert.ok((await buyAmenity(await member(), pool, 'G-AMEN')).discount_id);
    await assert.rejects(buyPackage(await user(), monthly, 'G-AMEN'), /code_not_for_item/);
  });
  await test('one use per person, in plain words: a second purchase with the same code is refused', async () => {
    const m = await member();
    await buyAmenity(m, pool, 'G-AMEN');
    await assert.rejects(buyAmenity(m, sauna, 'G-AMEN'), /code_already_used/);
  });
  await test('G1: turning a code off stops it and hides it; turning it on brings it back; only managers can', async () => {
    const id = await create(admin, 'G-PAUSE', [{ kind: 'package', id: null }]);
    await as(sub, 'select set_discount_paused($1,true)', [id]);
    const m = await user();
    await assert.rejects(buyPackage(m, monthly, 'G-PAUSE'), /invalid_code/);
    assert.ok(!(await codesFor(m)).includes('G-PAUSE'));
    assert.equal((await row('G-PAUSE')).archived_at, null);   // off is not archived: it stays on the Discounts page
    await as(admin, 'select set_discount_paused($1,false)', [id]);
    assert.ok((await codesFor(m)).includes('G-PAUSE'));
    for (const who of [desk, m]) await assert.rejects(as(who, 'select set_discount_paused($1,true)', [id]), /forbidden/);
  });
  await test('G1: an archived or expired code cannot be turned on or off', async () => {
    const id = await create(admin, 'G-ARCH', [{ kind: 'package', id: null }]);
    await as(admin, `select archive_catalog_record('code',$1,'Season over')`, [id]);
    await assert.rejects(as(admin, 'select set_discount_paused($1,false)', [id]), /record_archived/);
    await assert.rejects(as(admin, 'select set_discount_paused(999999,true)'), /not_found/);
  });
  await test('G1: a code no payment ever named can be deleted, items and all; a System admin only', async () => {
    const id = await create(admin, 'G-DEL', [{ kind: 'package', id: null }, { kind: 'amenity', id: pool }]);
    await assert.rejects(as(sub, 'select delete_discount($1)', [id]), /forbidden/);
    await as(admin, 'select delete_discount($1)', [id]);
    assert.equal(await row('G-DEL'), undefined);
    assert.equal((await db.query('select count(*)::int n from discount_items where discount_id=$1', [id])).rows[0].n, 0);
    const log = (await db.query(`select details from audit_log where action='discount.delete' and entity_id=$1`, [String(id)])).rows;
    assert.deepEqual(log.map(l => l.details.code), ['G-DEL']);
  });
  await test('G1: any payment, even a rejected one, keeps the code from being deleted', async () => {
    const id = await create(admin, 'G-USED', [{ kind: 'package', id: null }]);
    const pay = await buyPackage(await user(), monthly, 'G-USED');
    await as(desk, `select verify_payment($1,false,'Wrong reference')`, [pay.payment_id]);
    await assert.rejects(as(admin, 'select delete_discount($1)', [id]), /discount_used/);
    assert.ok(await row('G-USED'));
    await as(admin, `select archive_catalog_record('code',$1,'No longer offered')`, [id]);   // archiving still works
  });
  await test('G4: members see shown, live codes they can still use, with what each fits', async () => {
    await create(admin, 'G-SHOWN', [{ kind: 'package', id: monthly }, { kind: 'amenity', id: null }]);
    await create(admin, 'G-HIDDEN', [{ kind: 'package', id: null }], { show: false });
    await create(admin, 'G-FULL', [{ kind: 'package', id: null }], { limit: 1 });
    await buyPackage(await user(), monthly, 'G-FULL');
    const me = await user();
    await create(admin, 'G-MINE', [{ kind: 'package', id: null }]);
    await buyPackage(me, monthly, 'G-MINE');
    const rows = await as(me, 'select * from my_discount_codes()');
    const codes = rows.map(r => r.code);
    for (const c of ['G-SHOWN', 'G-MONTH', 'G-AMEN']) assert.ok(codes.includes(c), c);
    for (const c of ['G-HIDDEN', 'G-FULL', 'G-MINE', 'G-ARCH', 'G-DEL']) assert.ok(!codes.includes(c), c);
    const shown = rows.find(r => r.code === 'G-SHOWN');
    assert.deepEqual([shown.percent, shown.all_packages, shown.package_ids.map(String), shown.all_amenities, shown.amenity_ids],
      [10, false, [String(monthly)], true, []]);
    assert.ok(shown.expires_at && shown.created_at);
    assert.deepEqual(Object.keys(shown).sort(), ['all_amenities', 'all_packages', 'amenity_ids', 'code', 'created_at', 'expires_at', 'package_ids', 'percent']);
  });
  await test('G4: an inactive account gets nothing and anonymous cannot call it', async () => {
    const gone = await user(); await db.query('update profiles set is_active=false where id=$1', [gone]);
    await assert.rejects(as(gone, 'select * from my_discount_codes()'), /forbidden/);
    await db.query('begin'); await db.query('set local role anon');
    await assert.rejects(db.query('select * from my_discount_codes()'), /permission denied/);
    await db.query('rollback');
  });
  await test('staff who manage codes read the items; front desk and members do not', async () => {
    assert.ok((await as(sub, 'select * from discount_items')).length > 0);
    assert.equal((await as(desk, 'select * from discount_items')).length, 0);
    assert.equal((await as(await user(), 'select * from discount_items')).length, 0);
  });
} finally {
  console.log(`\n${passed} passed, ${failed} failed`);
  await db.end(); await server.stop();
  if (failed) process.exitCode = 1;
}
