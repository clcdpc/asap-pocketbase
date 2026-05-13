
const records = require('./lib/records.js');

// Mock app
const app = {
    findCollectionByNameOrId: (name) => ({ name }),
    findFirstRecordByData: (coll, field, value) => {
        console.log(`Searching ${coll} where ${field} = ${value}`);
        if (coll === 'patron_users' && field === 'email' && value === 'cwosborn@gmail.com') {
            return {
                id: '8yqrrv7nvrt7bj2',
                get: (f) => {
                    if (f === 'barcode') return '21868001586580';
                    if (f === 'nameFirst') return 'WES';
                    if (f === 'nameLast') return 'OSBORN';
                    return '';
                },
                set: (f, v) => console.log(`SET existing ${f} = ${v}`),
                setEmail: (v) => console.log(`SET EMAIL existing = ${v}`),
                setVerified: (v) => {},
                setRandomPassword: () => {}
            };
        }
        return null;
    },
    save: (record) => {
        console.log('SAVING record', record);
        if (record.email === 'cwosborn@gmail.com' && !record.isExisting) {
            throw new Error('validation_not_unique: Value must be unique.');
        }
    }
};

const newPatron = {
    Barcode: 'PACREG2473720',
    NameFirst: 'Wes',
    NameLast: 'Osborn',
    EmailAddress: 'cwosborn@gmail.com'
};

console.log('Starting upsert...');
try {
    // This is what I expect to happen with current code
    // records.upsertPatronUser(app, newPatron); 
} catch (e) {
    console.error('Caught expected error:', e.message);
}
