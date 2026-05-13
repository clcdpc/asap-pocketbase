
global.__hooks = '/Users/wesosborn/Downloads/suggest-a-purchase/pb_hooks';
const records = require('../lib/records.js');

class MockRecord {
    constructor(data = {}) {
        this.data = data;
        this.emailVal = data.email || '';
    }
    get(field) { return this.data[field]; }
    getInt(field) { return parseInt(this.data[field] || 0, 10); }
    getBool(field) { return !!this.data[field]; }
    set(field, val) { this.data[field] = val; }
    setEmail(email) { this.emailVal = email; }
    email() { return this.emailVal; }
    setRandomPassword() {}
    setVerified() {}
}

const db = {
    patron_users: [
        { id: 'existing', barcode: '21868001586580', email: 'cwosborn@gmail.com', nameFirst: 'WES', nameLast: 'OSBORN' }
    ]
};

const app = {
    findCollectionByNameOrId: (name) => ({ id: name, name: name }),
    findFirstRecordByData: (coll, field, value) => {
        return db[coll]?.find(r => r[field] === value) ? new MockRecord(db[coll].find(r => r[field] === value)) : null;
    },
    save: (record) => {
        console.log('Saving record:', JSON.stringify(record.data), 'Email field:', record.email());
        if (record.id === 'existing') {
            const idx = db.patron_users.findIndex(r => r.id === 'existing');
            db.patron_users[idx] = { ...record.data, email: record.email() };
        } else {
            db.patron_users.push({ ...record.data, email: record.email(), id: 'new' });
        }
    }
};

// Global Record class for the migration/records context
global.Record = MockRecord;

const sharedEmail = 'cwosborn@gmail.com';

console.log('--- Test 1: Shared email for a DIFFERENT person ---');
const differentPerson = {
    Barcode: 'OTHER123',
    NameFirst: 'John',
    NameLast: 'Doe',
    EmailAddress: sharedEmail
};
const rec1 = records.upsertPatronUser(app, differentPerson);
console.log('Result 1 - Barcode:', rec1.get('barcode'), 'Auth Email:', rec1.email(), 'Notification Email:', rec1.get('notificationEmail'));

console.log('\n--- Test 2: Shared email for the SAME person (barcode change) ---');
const samePersonNewBarcode = {
    Barcode: 'PACREG2473720',
    NameFirst: 'Wes',
    NameLast: 'Osborn',
    EmailAddress: sharedEmail
};
const rec2 = records.upsertPatronUser(app, samePersonNewBarcode);
console.log('Result 2 - ID:', rec2.get('id') || 'new', 'Barcode:', rec2.get('barcode'), 'Auth Email:', rec2.email(), 'Notification Email:', rec2.get('notificationEmail'));

console.log('\n--- Test 3: Create suggestion for person with shared email ---');
const suggestionData = { title: 'Test Book' };
const suggestion = records.createSuggestion(app, rec2, suggestionData, { email: sharedEmail });
console.log('Suggestion Email:', suggestion.get('email'));

if (suggestion.get('email') === sharedEmail) {
    console.log('\nSUCCESS: Shared email handled correctly!');
} else {
    console.log('\nFAILURE: Shared email not preserved on suggestion.');
}
