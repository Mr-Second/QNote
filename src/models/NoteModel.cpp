#include "NoteModel.h"

NoteModel::NoteModel(QObject *parent)
    : QAbstractListModel(parent)
{
}

int NoteModel::rowCount(const QModelIndex &parent) const
{
    if (parent.isValid())
        return 0;
    return m_notes.size();
}

QVariant NoteModel::data(const QModelIndex &index, int role) const
{
    if (!index.isValid() || index.row() < 0 || index.row() >= m_notes.size())
        return {};

    const Note &note = m_notes.at(index.row());

    switch (role) {
    case IdRole: return note.id;
    case TitleRole: return note.title;
    case ContentRole: return note.content;
    case DigestRole: return Note::stripHtml(note.content);
    case CategoryRole: return note.category;
    case CreatedAtRole: return note.createdAt;
    case UpdatedAtRole: return note.updatedAt;
    case TitleManualRole: return note.titleManual;
    case SortOrderRole: return note.sortOrder;
    default: return {};
    }
}

QHash<int, QByteArray> NoteModel::roleNames() const
{
    return {
        {IdRole, "id"},
        {TitleRole, "title"},
        {ContentRole, "content"},
        {DigestRole, "digest"},
        {CategoryRole, "category"},
        {CreatedAtRole, "createdAt"},
        {UpdatedAtRole, "updatedAt"},
        {TitleManualRole, "titleManual"},
        {SortOrderRole, "sortOrder"}
    };
}

void NoteModel::setNotes(const QList<Note> &notes)
{
    beginResetModel();
    m_notes = notes;
    endResetModel();
}

qint64 NoteModel::noteIdAt(int row) const
{
    if (row < 0 || row >= m_notes.size())
        return -1;
    return m_notes.at(row).id;
}

QString NoteModel::getNoteContent(qint64 id) const
{
    for (const auto &note : m_notes) {
        if (note.id == id)
            return note.content;
    }
    return {};
}

QString NoteModel::getNoteUpdatedAt(qint64 id) const
{
    for (const auto &note : m_notes) {
        if (note.id == id)
            return note.updatedAt.toString(QStringLiteral("yyyy/MM/dd hh:mm"));
    }
    return {};
}

bool NoteModel::getNoteTitleManual(qint64 id) const
{
    for (const auto &note : m_notes) {
        if (note.id == id)
            return note.titleManual;
    }
    return false;
}

QString NoteModel::getNoteTitle(qint64 id) const
{
    for (const auto &note : m_notes) {
        if (note.id == id)
            return note.title;
    }
    return {};
}
