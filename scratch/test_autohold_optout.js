const records = require('./lib/records.js');
const jobs = require('./lib/jobs.js');
const config = require('./lib/config.js');

try {
    const app = $app;
    const patronBarcode = '21868001586580';
    const patron = app.findFirstRecordByFilter('patron_library_settings', 'barcode = {:barcode}', { barcode: patronBarcode });
    const orgId = patron.get('libraryOrgId');

    console.log('Testing with Patron:', patronBarcode, 'Library:', orgId);

    // 1. Create a suggestion with autohold: false
    const suggestionData = {
        title: 'TEST AUTOHOLD OPT-OUT ' + new Date().getTime(),
        author: 'Test Author',
        format: 'book',
        publication: 'Already published',
        autohold: false,
        notes: 'Testing patron opt-out'
    };

    // Need to ensure the library has opt-out enabled for createSuggestion to honor it
    const wfRecord = records.recordForScope(app, 'workflow_settings', 'library', orgId);
    wfRecord.set('allowPatronAutoholdOptOut', true);
    app.save(wfRecord);

    const record = records.createSuggestion(app, patron, suggestionData);
    console.log('Created record ID:', record.id, 'Autohold value:', record.getBool('autohold'));

    if (record.getBool('autohold') !== false) {
        throw new Error('Record autohold should be false but is ' + record.get('autohold'));
    }

    // 2. Move it to pending_hold to simulate it being ready for hold placement
    record.set('status', records.STATUS.PENDING_HOLD);
    record.set('bibid', '1234567'); // Dummy BIB ID
    app.save(record);

    // 3. Process pending holds
    console.log('Processing pending holds...');
    const result = jobs.processPendingHolds(app);
    console.log('Process result:', JSON.stringify(result));

    // 4. Verify record status and note
    const updatedRecord = app.findRecordById('title_requests', record.id);
    console.log('Updated status:', updatedRecord.get('status'));
    console.log('Notes:', updatedRecord.get('notes'));

    if (updatedRecord.get('status') !== records.STATUS.HOLD_PLACED) {
         throw new Error('Record status should be hold_placed but is ' + updatedRecord.get('status'));
    }
    
    if (updatedRecord.get('notes').indexOf('SKIP: Patron opted out') === -1) {
        throw new Error('System note was not appended correctly');
    }

    console.log('SUCCESS: Patron autohold opt-out logic verified.');

} catch (err) {
    console.error('TEST FAILED:', err.message);
    console.error(err.stack);
}
