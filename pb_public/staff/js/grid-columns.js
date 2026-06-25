import { escapeAttr, formatNote, formatPublication, formatStandardDate } from './grid-utils.js';

export const NOTES_COLUMN_WIDTH = '110px';

export function getActionsColumnWidth(status) {
  if (status === 'additional_copies') return '100px';
  if (status === 'suggestion') return '180px';
  if (status === 'outstanding_purchase') return '160px';
  return '100px';
}

export function getGridColumns(status, rowById = new Map(), ctx) {
  const rowFor = (id) => rowById.get(id) || {};

  const barcodeColumn = {
    id: 'barcode',
    name: 'Barcode',
    width: '160px',
    formatter: (cell, row) => ctx.renderBarcodeCell(rowFor(row.cells[0].data))
  };

  const titleColumn = {
    id: 'title',
    name: 'Title',
    width: '310px',
    formatter: (cell, row) => ctx.renderTitleCell(rowFor(row.cells[0].data))
  };

  const authorColumn = {
    id: 'author',
    name: 'Author',
    width: '170px',
    formatter: (cell, row) => ctx.renderAuthorCell(rowFor(row.cells[0].data))
  };

  const formatColumn = {
    id: 'format',
    name: 'Format',
    width: '100px',
    formatter: (cell, row) => escapeAttr(ctx.formatMap[rowFor(row.cells[0].data).format] || rowFor(row.cells[0].data).format || '')
  };

  const identifierColumn = {
    id: 'identifier',
    name: 'ID/ISBN',
    width: '150px',
    sort: false,
    formatter: (cell, row) => escapeAttr(rowFor(row.cells[0].data).identifier || '')
  };

  const timingColumn = {
    id: 'publication',
    name: 'Timing',
    width: '100px',
    formatter: (cell, row) => escapeAttr(formatPublication(rowFor(row.cells[0].data).publication))
  };

  const submittedColumn = {
    id: 'submitted',
    name: 'Submitted',
    width: '100px',
    formatter: (cell, row) => escapeAttr(formatStandardDate(rowFor(row.cells[0].data).created))
  };

  const claimedColumn = {
    id: 'claimedBy',
    name: 'Claimed by',
    width: '110px',
    formatter: (cell, row) => ctx.renderClaimCell(rowFor(row.cells[0].data))
  };

  const notesColumnDef = {
    id: 'notes',
    name: 'Notes',
    width: NOTES_COLUMN_WIDTH,
    sort: false,
    formatter: (cell, row) => formatNote(rowFor(row.cells[0].data))
  };

  const actionsColumn = {
    id: 'actions',
    name: 'Actions',
    width: getActionsColumnWidth(status),
    sort: false,
    formatter: (cell, row) => gridjs.html(ctx.renderRowActions(rowFor(row.cells[0].data)))
  };

  const idColumn = {
    id: 'id',
    name: 'ID',
    hidden: true
  };

  if (status === 'additional_copies') {
    return [
      idColumn,
      titleColumn,
      authorColumn,
      {
        id: 'bibid',
        name: 'BIB ID',
        width: '100px',
        sort: false,
        formatter: (cell, row) => ctx.renderBibIdCell(rowFor(row.cells[0].data))
      },
      formatColumn,
      claimedColumn,
      {
        id: 'createdBy',
        name: 'Created by',
        width: '120px',
        formatter: (cell, row) => escapeAttr(rowFor(row.cells[0].data).createdByUsername || '')
      },
      notesColumnDef,
      actionsColumn
    ];
  }

  if (status === 'suggestion') {
    return [
      idColumn,
      barcodeColumn,
      titleColumn,
      authorColumn,
      identifierColumn,
      formatColumn,
      timingColumn,
      submittedColumn,
      claimedColumn,
      notesColumnDef,
      actionsColumn
    ];
  }

  if (status === 'closed') {
    return [
      idColumn,
      barcodeColumn,
      titleColumn,
      authorColumn,
      formatColumn,
      submittedColumn,
      {
        id: 'closeReason',
        name: 'Closed reason',
        width: '140px',
        formatter: (cell, row) => escapeAttr(ctx.closeReasonMap[rowFor(row.cells[0].data).closeReason] || rowFor(row.cells[0].data).closeReason || '')
      },
      claimedColumn,
      notesColumnDef,
      actionsColumn
    ];
  }

  const baseCols = [
    idColumn,
    barcodeColumn,
    titleColumn,
    authorColumn,
    identifierColumn,
    {
      id: 'bibid',
      name: 'BIB ID',
      width: '100px',
      sort: false,
      formatter: (cell, row) => ctx.renderBibIdCell(rowFor(row.cells[0].data))
    },
    formatColumn
  ];

  if (status !== 'pending_hold' && status !== 'hold_placed') {
    baseCols.push(timingColumn);
  }

  baseCols.push(submittedColumn, claimedColumn, notesColumnDef, actionsColumn);
  return baseCols;
}
