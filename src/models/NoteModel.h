#pragma once

#include "models/Note.h"

#include <QAbstractListModel>
#include <QList>

class NoteModel : public QAbstractListModel {
    Q_OBJECT
public:
    enum Roles {
        IdRole = Qt::UserRole + 1,
        TitleRole,
        ContentRole,
        DigestRole,
        CategoryRole,
        CreatedAtRole,
        UpdatedAtRole,
        TitleManualRole,
        SortOrderRole
    };

    explicit NoteModel(QObject *parent = nullptr);

    int rowCount(const QModelIndex &parent = {}) const override;
    QVariant data(const QModelIndex &index, int role) const override;
    QHash<int, QByteArray> roleNames() const override;

    void setNotes(const QList<Note> &notes);
    qint64 noteIdAt(int row) const;
    Q_INVOKABLE QString getNoteContent(qint64 id) const;
    Q_INVOKABLE QString getNoteUpdatedAt(qint64 id) const;
    Q_INVOKABLE bool getNoteTitleManual(qint64 id) const;
    Q_INVOKABLE QString getNoteTitle(qint64 id) const;

private:
    QList<Note> m_notes;
};
