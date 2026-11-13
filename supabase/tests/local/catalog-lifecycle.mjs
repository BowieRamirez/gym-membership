// Items 7, 8 and 9 of the 2026-11-12 plan (G6, G7, G9; 0061). Packages and amenities added from the app start as drafts: editable,
// deletable, never shown to members and never sold. Publishing checks the required fields. A published package is locked; changes go
// through a new version (a draft copy) whose publishing archives the old one in the same step. Members keep the terms they bought and
// renew into the newest version at its price. A published amenity stays editable (G7). Delete is only for a draft; anything else archives.
import { start, openCashShifts } from './harness.mjs';
import assert from 'node:assert/strict';

let oldPackage, oldAmenity;
const { server, client: db } = await start({ beforeMigration: async (client, file) => {
  if (file.startsWith('0061_')) {   // on sale before 0061: must stay on sale
    oldPackage = (await client.query(`insert into membership_packages(name,price,duration_days) values('L Before',800,30) returning membership_package_id`)).rows[0].membership_package_id;
    oldAmenity = (await client.query(`insert into amenities(name,price,duration_days) values('L Before Pool',200,30) returning amenity_id`)).rows[0].amenity_id;
  }
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
    [`l-${++serial}@test.io`, JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}

const admin = await user('admin'), desk = await user('employee'), coach = await user('coach');
await openCashShifts(db);
const draftPackage = async (name, extra = {}) => Number((await as(admin,
  `insert into membership_packages(name,description,price,duration_days,benefits) values($1,$2,$3,$4,$5) returning membership_package_id id`,
  [name, extra.description ?? 'A plan', extra.price ?? 1000, extra.days ?? 30, extra.benefits ?? ['Gym floor']]))[0].id);
const draftAmenity = async (name, description = 'Lanes') => Number((await as(admin,
  `insert into amenities(name,description,price,duration_days) values($1,$2,300,30) returning amenity_id id`, [name, description]))[0].id);
const publish = (kind, id, who = admin) => as(who, 'select publish_catalog_item($1,$2)', [kind, id]);
const newVersion = (id, who = admin) => as(who, 'select new_package_version($1) id', [id]).then(r => Number(r[0].id));
const remove = (kind, id, who = admin) => as(who, 'select delete_catalog_item($1,$2)', [kind, id]);
const pkgRow = async id => (await db.query('select * from membership_packages where membership_package_id=$1', [id])).rows[0];
const buyPackage = async (uid, p) => (await as(uid, 'select * from extend_membership($1)', [p]))[0];
const member = async p => { const m = await user(); const pay = await buyPackage(m, p); await as(desk, 'select verify_payment($1,true)', [pay.payment_id]); return m; };
const publishedPackage = async (name, extra) => { const id = await draftPackage(name, extra); await publish('plan', id); return id; };

try {
  await test('packages and amenities on sale before 0061 are published and still sell', async () => {
    assert.ok((await pkgRow(oldPackage)).published_at);
    assert.ok((await db.query('select published_at from amenities where amenity_id=$1', [oldAmenity])).rows[0].published_at);
    await member(oldPackage);
  });
  await test('a package or amenity added from the app starts as a draft', async () => {
    const p = await draftPackage('L Draft Start'), a = await draftAmenity('L Draft Pool');
    assert.equal((await pkgRow(p)).published_at, null);
    assert.equal((await db.query('select published_at from amenities where amenity_id=$1', [a])).rows[0].published_at, null);
    const forged = Number((await as(admin, `insert into membership_packages(name,price,duration_days,published_at) values('L Forged',100,30,now()) returning membership_package_id id`))[0].id);
    assert.equal((await pkgRow(forged)).published_at, null);
  });
  await test('members never see drafts', async () => {
    const p = await draftPackage('L Hidden'), a = await draftAmenity('L Hidden Pool'), m = await user();
    assert.equal((await as(m, 'select 1 from membership_packages where membership_package_id=$1', [p])).length, 0);
    assert.equal((await as(m, 'select 1 from amenities where amenity_id=$1', [a])).length, 0);
    assert.equal((await as(admin, 'select 1 from membership_packages where membership_package_id=$1', [p])).length, 1);
  });
  await test('a draft can be edited but not bought, at the app or at the desk', async () => {
    const p = await draftPackage('L Not Yet'), a = await draftAmenity('L Not Yet Pool');
    await as(admin, `update membership_packages set price=900,benefits='{Lockers}' where membership_package_id=$1`, [p]);
    assert.equal(Number((await pkgRow(p)).price), 900);
    await assert.rejects(buyPackage(await user(), p), /item_not_for_sale/);
    await assert.rejects(as(desk, `select record_counter_payment('membership','cash',$1,$2)`, [await user(), p]), /item_not_for_sale/);
    const m = await member(oldPackage);
    await assert.rejects(as(m, 'select avail_amenity($1)', [a]), /item_not_for_sale/);
    await assert.rejects(as(desk, `select record_counter_payment('amenity','cash',$1,$2)`, [m, a]), /item_not_for_sale/);
  });
  await test('publishing checks the required fields and is audited', async () => {
    const p = await draftPackage('L Fields', { description: ' ' }), a = await draftAmenity('L Fields Pool', null);
    await assert.rejects(publish('plan', p), /missing_fields/);
    await assert.rejects(publish('amenity', a), /missing_fields/);
    await as(admin, `update membership_packages set description='Full access' where membership_package_id=$1`, [p]);
    await publish('plan', p);
    assert.ok((await pkgRow(p)).published_at);
    await assert.rejects(publish('plan', p), /already_published/);
    assert.equal((await db.query(`select count(*)::int n from audit_log where action='catalog.publish' and entity_id=$1`, [String(p)])).rows[0].n, 1);
    await member(p);
  });
  await test('a published package is locked; archiving still works', async () => {
    const p = await publishedPackage('L Locked');
    for (const set of [`price=1`, `name='L Renamed'`, `description='x'`, `duration_days=1`, `benefits='{}'`, `image_path='x.png'`, `published_at=null`])
      await assert.rejects(as(admin, `update membership_packages set ${set} where membership_package_id=$1`, [p]), /package_published/);
    await as(admin, `select archive_catalog_record('plan',$1,'Retired')`, [p]);
    assert.equal((await pkgRow(p)).is_active, false);
  });
  await test('an archived package never comes back on sale; an archived draft can', async () => {
    const p = await publishedPackage('L Gone For Good');
    await as(admin, `select archive_catalog_record('plan',$1,'Retired')`, [p]);
    await assert.rejects(as(admin, `select restore_archived_record('plan',$1)`, [String(p)]), /package_archived/);
    await assert.rejects(buyPackage(await user(), p), /not_found|item_not_for_sale/);
    const d = await draftPackage('L Archived Draft');
    await as(admin, `select archive_catalog_record('plan',$1,'Not now')`, [d]);
    await as(admin, `select restore_archived_record('plan',$1)`, [String(d)]);
  });
  await test('a published amenity stays editable (G7)', async () => {
    const a = await draftAmenity('L Open Pool'); await publish('amenity', a);
    await as(admin, `update amenities set price=350,description='Six lanes' where amenity_id=$1`, [a]);
    assert.equal(Number((await db.query('select price from amenities where amenity_id=$1', [a])).rows[0].price), 350);
  });
  await test('one published package per name; a draft may share it', async () => {
    await publishedPackage('L Same Name');
    const twin = await draftPackage('L Same Name');
    await assert.rejects(publish('plan', twin), /name_taken/);
  });
  await test('a new version copies everything into a linked draft; only one at a time', async () => {
    const p = await publishedPackage('L Versioned', { description: 'Old words', price: 1000, days: 30, benefits: ['Gym floor', 'Lockers'] });
    await db.query(`update membership_packages set image_path='packages/v.png' where membership_package_id=$1`, [p]);
    const v = await newVersion(p);
    const [o, n] = [await pkgRow(p), await pkgRow(v)];
    assert.deepEqual([n.name, n.description, Number(n.price), n.duration_days, n.benefits, n.image_path, String(n.replaces_id), n.published_at],
      [o.name, o.description, Number(o.price), o.duration_days, o.benefits, o.image_path, String(p), null]);
    await assert.rejects(newVersion(p), /draft_exists/);
    await assert.rejects(newVersion(v), /not_published/);
    await member(p);   // the current version stays on sale while the new one is a draft
  });
  await test('publishing a version archives the old one in the same step; members keep their terms', async () => {
    const p = await publishedPackage('L Swap', { price: 1000, days: 30 });
    const m = await member(p);
    const terms = (await db.query('select * from membership_terms where member_id=$1', [m])).rows;
    const v = await newVersion(p);
    await as(admin, `update membership_packages set price=1500,duration_days=60 where membership_package_id=$1`, [v]);
    await publish('plan', v);
    assert.equal((await pkgRow(p)).is_active, false);
    assert.ok((await pkgRow(v)).published_at);
    assert.deepEqual((await db.query('select * from membership_terms where member_id=$1', [m])).rows, terms);
    await assert.rejects(buyPackage(await user(), p), /not_found|item_not_for_sale/);
  });
  await test('renewing an old version renews into the newest version at its price, in the app and at the desk', async () => {
    const p = await publishedPackage('L Renew', { price: 1000, days: 30 });
    const m1 = await member(p), m2 = await member(p);
    const v1 = await newVersion(p); await as(admin, `update membership_packages set price=1200 where membership_package_id=$1`, [v1]); await publish('plan', v1);
    const v2 = await newVersion(v1); await as(admin, `update membership_packages set price=1400,duration_days=45 where membership_package_id=$1`, [v2]); await publish('plan', v2);
    const ump = async m => (await db.query('select user_membership_package_id id from user_membership_packages where member_id=$1', [m])).rows[0].id;
    const app = (await as(m1, 'select * from renew_membership($1)', [await ump(m1)]))[0];
    assert.deepEqual([Number(app.amount), String(app.membership_package_snapshot_id), app.membership_duration_days], [1400, String(v2), 45]);
    const counter = (await as(desk, `select * from record_counter_payment('renewal','cash',$1,$2)`, [m2, await ump(m2)]))[0];
    assert.deepEqual([Number(counter.amount), String(counter.membership_package_snapshot_id), counter.membership_duration_days], [1400, String(v2), 45]);
    const ren = (await db.query('select new_starts_at,new_ends_at from membership_renewals where renewal_id=$1', [counter.renewal_id])).rows[0];
    assert.equal((ren.new_ends_at - ren.new_starts_at) / 86400000, 45);
  });
  await test('a draft is deleted for real; a published or archived one archives instead', async () => {
    const p = await publishedPackage('L Keep');
    const v = await newVersion(p);
    await remove('plan', v);
    assert.equal(await pkgRow(v), undefined);
    assert.ok((await pkgRow(p)).published_at);
    assert.equal((await pkgRow(p)).is_active, true);
    await assert.rejects(remove('plan', p), /published_archive_instead/);
    await newVersion(p);   // the draft slot is free again
    const a = await draftAmenity('L Bin Pool'); await remove('amenity', a);
    assert.equal((await db.query('select count(*)::int n from amenities where amenity_id=$1', [a])).rows[0].n, 0);
    await assert.rejects(remove('amenity', oldAmenity), /published_archive_instead/);
    assert.equal((await db.query(`select count(*)::int n from audit_log where action='catalog.delete' and entity_id=$1`, [String(v)])).rows[0].n, 1);
    await assert.rejects(remove('code', 1), /bad_input/);
  });
  await test('a discount code can only name published items', async () => {
    const p = await draftPackage('L Code Draft');
    await assert.rejects(as(admin, `select create_discount('LDRAFT',10,now()+interval '9 days',5,true,$1::jsonb)`, [JSON.stringify([{ kind: 'package', id: p }])]), /item_not_found/);
  });
  await test('members, coaches and the front desk cannot publish, version or delete', async () => {
    const p = await draftPackage('L Not Yours'), q = await publishedPackage('L Not Yours Either');
    for (const who of [await user(), coach, desk]) {
      await assert.rejects(publish('plan', p, who), /forbidden|42501|permission/);
      await assert.rejects(newVersion(q, who), /forbidden|42501|permission/);
      await assert.rejects(remove('plan', p, who), /forbidden|42501|permission/);
    }
    const m = await user();   // the calls written out, for rules.mjs's break-attempt check (D16)
    await assert.rejects(as(m, `select publish_catalog_item('plan',$1)`, [p]), /forbidden/);
    await assert.rejects(as(m, `select new_package_version($1)`, [q]), /forbidden/);
    await assert.rejects(as(m, `select delete_catalog_item('plan',$1)`, [p]), /forbidden/);
    for (const fn of ['publish_catalog_item(text,bigint)', 'new_package_version(bigint)', 'delete_catalog_item(text,bigint)']) {
      const r = (await db.query(`select has_function_privilege('anon','public.${fn}','execute') a`)).rows[0];
      assert.equal(r.a, false, fn);
    }
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Catalog lifecycle: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
