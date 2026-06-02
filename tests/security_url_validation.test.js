const assert = require('assert');
const fs = require('fs');
const path = require('path');

// Helper to extract function body from file
function extractFunction(filePath, functionName) {
    const content = fs.readFileSync(filePath, 'utf8');
    const regex = new RegExp(`export function ${functionName}\\(.*?\\) \\{[\\s\\S]*?^\\}`, 'm');
    const match = content.match(regex);
    if (!match) {
        throw new Error(`Could not find function ${functionName} in ${filePath}`);
    }
    return match[0].replace('export ', '');
}

// Extract functions
const renderEditLeapBibLinkSource = extractFunction(path.join(__dirname, '../pb_public/staff/js/modals.js'), 'renderEditLeapBibLink');
const renderBibIdCellSource = extractFunction(path.join(__dirname, '../pb_public/staff/js/grid.js'), 'renderBibIdCell');
const renderBarcodeCellSource = extractFunction(path.join(__dirname, '../pb_public/staff/js/grid.js'), 'renderBarcodeCell');

// Mock escapeAttr (simplified version for testing)
function escapeAttr(value) {
    return String(value || '')
        .replace(/&/g, '&amp;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;');
}

function runTests() {
    console.log('Running security URL validation tests...');

    // Test renderEditLeapBibLink
    const testRenderEditLeapBibLink = new Function('bibId', 'leapBibUrl', 'document', 'escapeAttr', `
        ${renderEditLeapBibLinkSource}
        renderEditLeapBibLink(bibId);
    `);

    const mockDocument = {
        elements: {},
        getElementById: function(id) {
            if (!this.elements[id]) {
                this.elements[id] = {
                    classList: {
                        classes: new Set(),
                        add: function(c) { this.classes.add(c); },
                        remove: function(c) { this.classes.delete(c); },
                        contains: function(c) { return this.classes.has(c); }
                    },
                    innerHTML: ''
                };
            }
            return this.elements[id];
        }
    };

    // Case 1: Safe URL
    mockDocument.elements = {};
    testRenderEditLeapBibLink('123', () => 'https://leap.example.com/123', mockDocument, escapeAttr);
    const container1 = mockDocument.getElementById('edit-leap-bib-link-container');
    assert.strictEqual(container1.classList.contains('hidden'), false, 'Should not be hidden for safe URL');
    assert.ok(container1.innerHTML.includes('href="https://leap.example.com/123"'), 'Should contain safe URL in href');

    // Case 2: Unsafe URL (javascript:)
    mockDocument.elements = {};
    testRenderEditLeapBibLink('123', () => 'javascript:alert(1)', mockDocument, escapeAttr);
    const container2 = mockDocument.getElementById('edit-leap-bib-link-container');
    assert.strictEqual(container2.classList.contains('hidden'), true, 'Should be hidden for javascript: URL');
    assert.strictEqual(container2.innerHTML, '', 'InnerHTML should be empty for unsafe URL');

    // Case 3: Empty URL
    mockDocument.elements = {};
    testRenderEditLeapBibLink('123', () => '', mockDocument, escapeAttr);
    const container3 = mockDocument.getElementById('edit-leap-bib-link-container');
    assert.strictEqual(container3.classList.contains('hidden'), true, 'Should be hidden for empty URL');

    // Test renderBibIdCell
    const testRenderBibIdCell = new Function('row', 'leapBibUrl', 'gridjs', 'escapeAttr', `
        ${renderBibIdCellSource}
        return renderBibIdCell(row);
    `);

    const mockGridjs = {
        html: (s) => ({ __html: s })
    };

    // Case 4: Safe URL in grid
    const cell1 = testRenderBibIdCell({ bibid: '123' }, () => 'https://leap.example.com/123', mockGridjs, escapeAttr);
    assert.strictEqual(typeof cell1, 'object', 'Should return gridjs.html object for safe URL');
    assert.ok(cell1.__html.includes('href="https://leap.example.com/123"'), 'Grid cell should contain safe URL');

    // Case 5: Unsafe URL in grid
    const cell2 = testRenderBibIdCell({ bibid: '123' }, () => 'javascript:alert(1)', mockGridjs, escapeAttr);
    assert.strictEqual(cell2, '123', 'Should fallback to plain text BIB ID for unsafe URL');

    // Case 6: No BIB ID
    const cell3 = testRenderBibIdCell({}, () => 'https://leap.example.com/', mockGridjs, escapeAttr);
    assert.strictEqual(cell3, '', 'Should return empty string for missing BIB ID');

    // Test renderBarcodeCell
    const testRenderBarcodeCell = new Function('row', 'leapPatronUrl', 'gridjs', 'escapeAttr', `
        ${renderBarcodeCellSource}
        return renderBarcodeCell(row);
    `);

    const patronCell1 = testRenderBarcodeCell({ barcode: '2900', polarisPatronId: 'p1' }, () => 'https://leap.example.com/patrons/p1', mockGridjs, escapeAttr);
    assert.strictEqual(typeof patronCell1, 'object', 'Should return gridjs.html object for barcode cell');
    assert.ok(patronCell1.__html.includes('href="https://leap.example.com/patrons/p1"'), 'Barcode cell should link safe patron URL');

    const patronCell2 = testRenderBarcodeCell({ barcode: '2900', polarisPatronId: 'p1' }, () => 'javascript:alert(1)', mockGridjs, escapeAttr);
    assert.ok(!patronCell2.__html.includes('javascript:alert'), 'Barcode cell should not render unsafe patron URL');
    assert.ok(patronCell2.__html.includes('<div class="barcode-text">2900</div>'), 'Barcode cell should fall back to plain barcode');

    console.log('All security URL validation tests passed!');
}

try {
    runTests();
} catch (error) {
    console.error('Test failed:', error);
    process.exit(1);
}
